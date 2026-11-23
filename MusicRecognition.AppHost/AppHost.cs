using Aspire.Hosting.Docker.Resources.ComposeNodes;

var builder = DistributedApplication.CreateBuilder(args);
var publishing = builder.ExecutionContext.IsPublishMode;
var dashboardKey = publishing ? builder.AddParameter("dashboard-api-key",
    new GenerateParameterDefault { MinLength = 64, Lower = true, Upper = true, Numeric = true, Special = false }, secret: true) : null;
var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard =>
    {
        dashboard.WithImageSHA256("cf82f4314b4b3e94285f9b6aa2141fd484377fd926df0ef11cf620e43bc39ba8").PublishAsDockerComposeService((_, service) =>
        {
            service.Ports.Clear(); service.Ports.Add("127.0.0.1:18888:18888");
            service.Restart = "unless-stopped";
            service.Deploy = new() { Resources = new() { Limits = new() { Cpus = "1", Memory = "512m" } } };
        });
        if (dashboardKey is not null)
            dashboard.WithEnvironment("Dashboard__Api__AuthMode", "ApiKey").WithEnvironment("Dashboard__Api__PrimaryApiKey", dashboardKey)
                .WithEnvironment("Dashboard__Frontend__AuthMode", "BrowserToken").WithEnvironment("Dashboard__Frontend__BrowserToken", dashboardKey);
    });

var postgres = builder.AddPostgres("recognition-postgres", port: 5432)
    .WithDataVolume(publishing ? "musicrecognition-deployed-postgres" : "musicrecognition-postgres-data");
var database = postgres.AddDatabase("recognitiondb");

var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
if (publishing)
{
    var apiContainer = builder.AddDockerfile("api", root, "deploy/backend.Dockerfile", "api")
        .WithOtlpExporter()
        .WithReference(database).WaitFor(database).WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithVolume("musicrecognition-deployed-scores", "/data")
        .WithEnvironment("Recognition__TimeoutSeconds", "900")
        .PublishAsDockerComposeService((_, service) => Harden(service, "2", "1g", false));
    builder.AddDockerfile("worker", root, "deploy/backend.Dockerfile", "worker")
        .WithOtlpExporter()
        .WithReference(database).WaitFor(database).WaitFor(apiContainer).WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithVolume("musicrecognition-deployed-scores", "/data")
        .WithEnvironment("Recognition__TimeoutSeconds", "900")
        .PublishAsDockerComposeService((_, service) => Harden(service, "2", "4g", false));
    builder.AddDockerfile("web", root, "deploy/web.Dockerfile")
        .WithHttpEndpoint(port: 3080, targetPort: 3000, name: "http")
        .WithEnvironment("NUXT_API_BASE", apiContainer.GetEndpoint("http")).WaitFor(apiContainer)
        .PublishAsDockerComposeService((_, service) => Harden(service, "1", "512m", true));
    postgres.PublishAsDockerComposeService((_, service) =>
    {
        service.Ports.Clear(); service.Restart = "unless-stopped";
        service.Deploy = new() { Resources = new() { Limits = new() { Cpus = "1", Memory = "512m" } } };
        service.Logging = new() { Driver = "json-file", Options = { ["max-size"] = "10m", ["max-file"] = "3" } };
        service.Healthcheck = new() { Test = ["CMD-SHELL", "pg_isready -U postgres"], Interval = "5s", Timeout = "3s", StartPeriod = "15s", Retries = 20 };
    });
    builder.Build().Run();
    return;
}
var projectPython = Path.Combine(root, ".tools", "poc-python", OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
var python = builder.Configuration["Recognition:Python"] ?? Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? projectPython;
if (python == projectPython && !File.Exists(projectPython))
    throw new InvalidOperationException("Prepare the project Python environment with scripts/setup/setup.ps1, or explicitly configure Recognition:Python / MUSICRECOGNITION_PYTHON.");

var api = builder.AddProject<Projects.MusicRecognition_Api>("api")
    .WithReference(database).WaitFor(database)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Recognition__RepositoryRoot", root)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithEnvironment("Recognition__DataRoot", Path.Combine(root, "output", "backend"))
    .WithEnvironment("Recognition__Python", python);

builder.AddProject<Projects.MusicRecognition_Worker>("worker")
    .WithReference(database).WaitFor(database).WaitFor(api)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Recognition__RepositoryRoot", root)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithEnvironment("Recognition__DataRoot", Path.Combine(root, "output", "backend"))
    .WithEnvironment("Recognition__Python", python);

builder.AddViteApp("web", "../MusicRecognition.Web")
    .WithHttpHealthCheck("/health")
    .WithHttpEndpoint(3000)
    .WithEnvironment("NUXT_API_BASE", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();

static void Harden(Service service, string cpus, string memory, bool external)
{
    service.Restart = "unless-stopped";
    service.Init = true;
    service.CapDrop.Add("ALL");
    service.ReadOnly = true;
    service.Tmpfs.Add("/tmp:rw,nosuid,nodev,exec,size=512m,mode=1777");
    if (!external) service.Tmpfs.Add("/home/app:rw,nosuid,nodev,exec,size=128m,uid=1654,gid=1654,mode=0700");
    service.SecurityOpt.Add("no-new-privileges:true");
    service.StopGracePeriod = "30s";
    service.Logging = new() { Driver = "json-file", Options = { ["max-size"] = "10m", ["max-file"] = "3" } };
    service.Deploy = new() { Resources = new() { Limits = new() { Cpus = cpus, Memory = memory } } };
    service.Healthcheck = new()
    {
        Interval = "15s",
        Timeout = "5s",
        StartPeriod = "90s",
        Retries = 5,
        Test = external
            ? ["CMD", "node", "-e", "fetch('http://127.0.0.1:3000/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"]
            : ["CMD", "/opt/recognition/.tools/poc-python/bin/python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health',timeout=4)"]
    };
    service.Ports.Clear();
    if (external) service.Ports.Add("127.0.0.1:3080:3000");
}
