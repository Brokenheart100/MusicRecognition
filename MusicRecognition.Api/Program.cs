using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using MusicRecognition.Api.Endpoints;
using MusicRecognition.Api.Extensions;
using MusicRecognition.Api.Middleware;
using MusicRecognition.Api.OpenApi;
using MusicRecognition.Infrastructure;
using MusicRecognition.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddRecognition(builder.Configuration, worker: false);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 21 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddRequestProtection(builder.Configuration);
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<ContractSchemaTransformer>();
    options.AddOperationTransformer<ContractOperationTransformer>();
});

var app = builder.Build();
app.UseResponseHeaders();
app.UseApiErrorHandling();
app.UseRateLimiter();
app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapScoreEndpoints();
await app.Services.MigrateRecognitionAsync();
app.Run();
