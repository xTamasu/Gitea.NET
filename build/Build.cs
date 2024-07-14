using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;

class Build : NukeBuild
{
public static int Main() => Execute<Build>(x => x.PublishClient);

    [Parameter("URL of the Swagger API")] readonly string Url;
    [Parameter("Docker image version for OpenAPI Generator", Name = "DockerVersion")] readonly string DockerVersion = "latest";
    [Parameter("Gitea Docker image version", Name = "giteaVersion")] readonly string GiteaVersion = "1.22.0";

    IEnumerable<AbsolutePath> FilesToClean () => RootDirectory.GlobDirectories("**/client");
    bool AnyFilesToClean () => FilesToClean().Any();

    const string GiteaContainerName = "gitea-swagger";
    const string OpenAPIContainerName = "gitea-openapi-generator";
    const int GiteaPort = 3000;

    Target CleanupFiles => _ => _
        .DependentFor(GenerateClient)
        .OnlyWhenDynamic(AnyFilesToClean)
        .Executes(() => {
            FilesToClean().ForEach(x => x.DeleteDirectory());
    });

    Target StartGitea => _ => _
        .Executes(() =>
        {
            DockerTasks.DockerRun(c => c
                .SetImage($"gitea/gitea:{GiteaVersion}")
                .SetName(GiteaContainerName)
                .SetDetach(true)
                .SetPublish($"{GiteaPort}:3000")
                .SetEnv("GITEA__database__DB_TYPE=sqlite3",
                    "GITEA__database__PATH=/data/gitea/gitea.db",
                    "GITEA__security__INSTALL_LOCK=true",
                    "GITEA__security__SECRET_KEY=your_secret_key",
                    "GITEA__admin__DEFAULT_ADMIN_NAME=admin",
                    "GITEA__admin__DEFAULT_ADMIN_PASSWORD=admin",
                    "GITEA__admin__DEFAULT_ADMIN_EMAIL=admin@example.com"));
            
            Log.Information("Waiting for Gitea to start...");
            CheckGiteaReadiness().Wait();
        });

    Target GenerateClient => _ => _
        .DependsOn(StartGitea, CleanupFiles)
        .Executes(() =>
        {
            var giteaSwaggerUrl = "http://localhost:3000/swagger.v1.json";

            var container = DockerTasks.DockerRun(c => c
                .SetImage($"openapitools/openapi-generator-cli:{DockerVersion}")
                .SetName(OpenAPIContainerName)
                .SetNetwork("host")
                .SetVolume($"{RootDirectory}:/local")
                .SetCommand("generate")
                .AddArgs("-i", giteaSwaggerUrl)
                .AddArgs("-g", "csharp")
                .AddArgs("-o", @"/local/client")
                .AddArgs("-c", @"/local/config.yaml")
                .AddArgs("--additional-properties", $"packageVersion={GiteaVersion}.3")
                );
        });

    Target PublishClient => _ => _
        .DependsOn(GenerateClient)
        .Executes(() => {
            DotNetTasks.DotNetPack(
                new DotNetPackSettings()
                .SetProject(RootDirectory/"client")
                .SetOutputDirectory(RootDirectory/"output")
                .SetDescription("A .NET client for interacting with the Gitea API generated from the Gitea OpenAPI definition.")
                .SetAuthors("Lukas Klepper")
                .SetPackageProjectUrl("https://github.com/xTamasu/Gitea.NET")
                .SetPackageReleaseNotes($"See https://github.com/go-gitea/gitea/releases/tag/v{GiteaVersion} for changelog.")
                .SetPackageIconUrl("https://raw.githubusercontent.com/xTamasu/Gitea.NET/main/Gitea_NET_Logo.svg")
                //.SetPackageId("Gitea.NET")
                .SetRepositoryUrl("https://github.com/xTamasu/Gitea.NET")
                .SetPackageLicenseUrl("https://github.com/xTamasu/Gitea.NET/blob/main/LICENSE")
                .SetCopyright("MIT")
            );
        });

    Target CleanupDocker => _ => _
        .TriggeredBy(GenerateClient)
        .Executes(() =>
        {
            DockerTasks.DockerStop(c => c
                .SetContainers(GiteaContainerName, OpenAPIContainerName));
            
            DockerTasks.DockerRm(new DockerRmSettings().SetContainers(GiteaContainerName, OpenAPIContainerName));
        });

    async Task CheckGiteaReadiness()
    {
        var giteaSwaggerUrl = $"http://localhost:{GiteaPort}/swagger.v1.json";
        using var httpClient = new HttpClient();
        
        for (int i = 0; i < 30; i++) // Try for up to 30 seconds
        {
            try
            {
                var response = await httpClient.GetAsync(giteaSwaggerUrl);
                if (response.IsSuccessStatusCode)
                {
                    Log.Information("Gitea is ready.");
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Ignore exceptions, retry until timeout
            }

            Log.Information("Waiting for Gitea to be ready...");
            await Task.Delay(1000);
        }

        throw new Exception("Gitea did not become ready in time.");
    }
}
