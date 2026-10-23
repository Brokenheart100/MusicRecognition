using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Api;

// Tighten the wire contract where CLR strings/arrays encode a finite domain or tuple.
public sealed class ContractSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(IFormFile))
        {
            schema.Type = JsonSchemaType.String; schema.Format = "binary";
            schema.Properties = null; schema.Required = null; schema.AdditionalPropertiesAllowed = false;
        }
        if (type.Namespace is "MusicRecognition.Application" or "MusicRecognition.Domain" && schema.Properties is { } props)
        {
            schema.Required = props.Keys.ToHashSet();
            if (type == typeof(Pitch))
            {
                foreach (var name in new[] { "midi", "diatonic" }) { props.Remove(name); schema.Required.Remove(name); }
                Range("alter", -2, 2); Range("octave", 0, 9);
            }
            if (type == typeof(JobView) || type == typeof(PageJobView)) Enum("state", "Queued", "Processing", "Cancelling", "Cancelled", "Ready", "Failed", "Partial");
            if (type == typeof(RevisionView) || type == typeof(DocumentExportView)) Enum("state", "Queued", "Processing", "Ready", "Failed");
            if (type == typeof(PageJobView)) Enum("exportState", "Queued", "Processing", "Ready", "Failed");
            if (type == typeof(AnnotationView))
            {
                Enum("status", "placed", "needs-manual-placement", "hidden");
                Length("baselineOffset", 2); Length("bounds", 4); Length("labelBounds", 4);
                if (props["dots"] is OpenApiSchema dots && dots.Items is OpenApiSchema dot) dot.MinItems = dot.MaxItems = 3;
            }
            if (type == typeof(PdfGeometryView)) Length("cropBox", 4);
            if (type == typeof(AnnotationStyle))
            {
                Range("fontSizePoints", 3, 16);
                if (props["color"] is OpenApiSchema color) { color.Pattern = "^#[0-9a-fA-F]{6}$"; color.MinLength = color.MaxLength = 7; }
            }
            foreach (var name in new[] { "version", "expectedRevision", "pageCount", "number", "startMeasure" }) Range(name, 1, null);
            foreach (var name in new[] { "attempts", "currentRevision" }) Range(name, 0, null);
            void Range(string name, int min, int? max) { if (props.TryGetValue(name, out var p) && p is OpenApiSchema value) { value.Minimum = min.ToString(); value.Maximum = max?.ToString(); } }
            void Enum(string name, params string[] values) { if (props.TryGetValue(name, out var p) && p is OpenApiSchema value) { value.Enum = values.Select(x => (JsonNode)JsonValue.Create(x)!).ToList(); if (value.Type?.HasFlag(JsonSchemaType.Null) == true) value.Enum.Add(null!); } }
            void Length(string name, int length) { if (props.TryGetValue(name, out var p) && p is OpenApiSchema value) value.MinItems = value.MaxItems = length; }
        }
        return Task.CompletedTask;
    }
}
