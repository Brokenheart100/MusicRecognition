var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("recognition-postgres")
    .WithDataVolume("musicrecognition-postgres-data")
    .AddDatabase("recognitiondb");
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var python = builder.Configuration["Recognition:Python"] ?? Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? "python";
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
builder.Build().Run();
