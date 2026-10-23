using MusicRecognition.Infrastructure;
using MusicRecognition.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddRecognition(builder.Configuration, worker: true);
var app = builder.Build();
await app.Services.GetRequiredService<RecognitionRuntime>().ValidateAsync(app.Lifetime.ApplicationStopping);
app.MapDefaultEndpoints();
await app.Services.MigrateRecognitionAsync();
app.Run();
