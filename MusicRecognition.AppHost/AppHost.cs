var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("recognition-postgres")
    .WithDataVolume("musicrecognition-postgres-data")
    .AddDatabase("recognitiondb");

var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var projectPython = Path.Combine(root, ".tools", "poc-python", OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
var python = builder.Configuration["Recognition:Python"] ?? Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? projectPython;
if (python == projectPython && !File.Exists(projectPython))
    throw new InvalidOperationException("Prepare the project Python environment with scripts/runtime/setup.ps1, or explicitly configure Recognition:Python / MUSICRECOGNITION_PYTHON.");

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
