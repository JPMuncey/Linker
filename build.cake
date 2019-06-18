#tool nuget:?package=GitVersion.CommandLine&version=4.0.0-beta0012
#tool nuget:?package=OctopusTools&version=6.7.0

#addin nuget:?package=Cake.Npm&version=0.17.0
#addin nuget:?package=Cake.Curl&version=4.1.0

#load build/paths.cake
#load build/urls.cake
#load build/version.cake
#load build/package.cake

var target = Argument("Target", "Version");
var octoDeployEnv = Argument("OctoDeployEnv", "Test");

Setup<PackageMetadata>(context =>
{
    return new PackageMetadata(
        outputDirectory: Argument("packageOutputDirectory", "packages"),
        name: "Linker-6"

    );
});

Task("Compile")
    .Does(() =>
{
    DotNetCoreBuild(Paths.SolutionFile.FullPath);
});

Task("Test")
    .IsDependentOn("Compile")
    .Does(() =>
{
    DotNetCoreTest(
        Paths.SolutionFile.FullPath,
        new DotNetCoreTestSettings
        {
            Logger = "trx",  // VSTest results format
            ResultsDirectory = Paths.TestResultsDirectory
        });
});

Task("Version")
    .Does<PackageMetadata>(package =>
{
    package.Version =  ReadVersionFromProjectFile(Context);
    
    if (package.Version == null)
    {
        Information("No version number found in csproj. Falling back to GitVersion.");
        package.Version = GitVersion().FullSemVer;
    }
    Information($"Version = {package.Version}");
});

Task("Build-Frontend")
    .Does(() =>
{
    NpmInstall(settings => settings.FromPath(Paths.FrontendDirectoryPath.FullPath));
    NpmRunScript("build", settings => settings.FromPath(Paths.FrontendDirectoryPath.FullPath));
});

Task("Package-Zip")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package =>
{
    CleanDirectory(package.OutputDirectory);

    package.Extension = "zip";

    DotNetCorePublish(
        Paths.WebProjectFile.GetDirectory().FullPath,
        new DotNetCorePublishSettings
        {
            OutputDirectory = Paths.PublishDirectory,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings
            {
                NoLogo = true
            }
        }

    );

    Zip(Paths.PublishDirectory, package.FullPath);
});

Task("Package-Octopus")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package =>
{
    CleanDirectory(package.OutputDirectory);

    package.Extension = "nupkg";

    DotNetCorePublish(
        Paths.WebProjectFile.GetDirectory().FullPath,
        new DotNetCorePublishSettings
        {
            OutputDirectory = Paths.PublishDirectory,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings
            {
                NoLogo = true
            }
        }

    );

    OctoPack(
        package.Name,
        new OctopusPackSettings
        {
            Format = OctopusPackFormat.NuPkg,
            Version = package.Version,
            BasePath = Paths.PublishDirectory, 
            OutFolder = package.OutputDirectory
        });
});

Task("Deploy-Kudu")
    .Description("Deploys zipfile to Kudu")
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package =>
    {
        CurlUploadFile(
            package.FullPath,
            Urls.KudoDeployUri,
            new CurlSettings
            {
                Username = EnvironmentVariable("DeploymentUser"),
                Password = EnvironmentVariable("DeploymentPassword"),
                RequestCommand = "POST",
                ProgressBar = true,
                //ArgumentCustomization = AssemblyLoadEventArgs=> AssemblyLoadEventArgs.Append("--fail")
            }
        );
    });

Task("Deploy-Octopus")
    .Description("Deploy app via Octopus")
    .IsDependentOn("Package-Octopus")
    .Does<PackageMetadata>(package =>
    {
        // Push the package to Octopus
        OctoPush(
            Urls.OctopusServerPackageRepo.AbsoluteUri,
            EnvironmentVariable("OctopusApiKey"),
            package.FullPath,
            new OctopusPushSettings
            {
                EnableServiceMessages = true
            }
        );

        // Trigger an Octopus deploy
        OctoCreateRelease(
                "Linker-6",
                new CreateReleaseSettings
                {
                    Server = Urls.OctopusServerPackageRepo.AbsoluteUri,
                    ApiKey = EnvironmentVariable("OctopusApiKey"),
                    ReleaseNumber = package.Version,
                    DefaultPackageVersion = package.Version,
                    DeployTo = octoDeployEnv,
                    IgnoreExisting = true,
                    DeploymentProgress = true,
                    WaitForDeployment = true
                }
        );

    });

Task("Set-Build-Number")
    .WithCriteria(() => BuildSystem.IsRunningOnAzurePipelinesHosted)
    .Does<PackageMetadata>(package =>
    {
        var buildNumber = TFBuild.Environment.Build.Number;
        TFBuild.Commands.UpdateBuildNumber($"{package.Version}+{buildNumber}");
    });

Task("Publish-Build-Artifact")
    .WithCriteria(() => BuildSystem.IsRunningOnAzurePipelinesHosted)
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package =>
    {
        TFBuild.Commands.UploadArtifactDirectory(package.OutputDirectory);

        // For TeamCity
        /*        
        foreach(var p in GetFiles(package.OutputDirectory + $"/*.{package.Extension}"))
        {
            TeamCity.PublishArtifacts(p.FullPath);
        }
        */
    });

Task("Publish-Test-Results")
    .WithCriteria(() => BuildSystem.IsRunningOnAzurePipelinesHosted)
    .IsDependentOn("Test")
    .Does(() =>
    {
        TFBuild.Commands.PublishTestResults(
            new TFBuildPublishTestResultsData
            {
                TestRunner = TFTestRunnerType.VSTest,
                TestResultsFiles = GetFiles(Paths.TestResultsDirectory + "/*.trx").ToList()
            }
        );
    });

Task("Build-CI")
    .IsDependentOn("Compile")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .IsDependentOn("Package-Zip")
    .IsDependentOn("Set-Build-Number")
    .IsDependentOn("Publish-Test-Results")
    .IsDependentOn("Publish-Build-Artifact");


RunTarget(target);