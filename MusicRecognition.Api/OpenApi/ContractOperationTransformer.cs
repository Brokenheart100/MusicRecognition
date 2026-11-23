using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MusicRecognition.Api.OpenApi;

internal sealed class ContractOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        foreach (var parameter in operation.Parameters ?? [])
            if (parameter.In == ParameterLocation.Header && parameter.Schema is OpenApiSchema header)
            {
                if (parameter.Name == "X-Score-Token") { header.MinLength = 32; header.MaxLength = 200; }
                if (parameter.Name == "Idempotency-Key") { header.MinLength = 8; header.MaxLength = 100; }
            }
        if (operation.OperationId == "ImportScore" && operation.RequestBody?.Content is { } multipart)
            foreach (var media in multipart.Values)
                if (media.Schema is OpenApiSchema form)
                    foreach (var part in form.AllOf ?? [])
                        if (part.Properties?.TryGetValue("preprocessing", out var property) == true && property is OpenApiSchema preprocessing)
                            preprocessing.Enum = new[] { "", "None", "Adaptive", "none", "adaptive" }.Select(x => (JsonNode)JsonValue.Create(x)!).ToList();
        if (operation.OperationId is "ImportScore" && operation.RequestBody?.Content is { } imports)
            foreach (var media in imports.Values)
                if (media.Schema is OpenApiSchema form)
                    foreach (var part in (form.AllOf ?? []).Append(form))
                        if (part.Properties?.TryGetValue("engine", out var property) == true && property is OpenApiSchema engine)
                            engine.Enum = new[] { "", "Audiveris", "Homr", "audiveris", "homr" }.Select(x => (JsonNode)JsonValue.Create(x)!).ToList();
        if (operation.OperationId is "GetScoreSource" or "GetAnnotatedPdf")
            SetBinaryResponse(operation, ["200", "206"], operation.OperationId == "GetAnnotatedPdf" ? "application/pdf" : "application/octet-stream");
        if (operation.OperationId is "GetScoreMusicXml")
            SetBinaryResponse(operation, ["200"], "application/vnd.recordare.musicxml");
        if (operation.OperationId is "GetRevisionAnnotations" or "GetRevisionMetrics")
            SetBinaryResponse(operation, ["200"], "application/json");
        return Task.CompletedTask;
    }

    private static void SetBinaryResponse(OpenApiOperation operation, string[] statuses, string contentType)
    {
        foreach (var status in statuses)
            operation.Responses![status] = new OpenApiResponse
            {
                Description = status == "200" ? "Artifact bytes." : "Partial artifact bytes.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    [contentType] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
                }
            };
    }
}
