using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using MusicRecognition.Api.Contracts.Responses;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Api.OpenApi;

// 这个文件只微调 OpenAPI 文档里的类型细节，不改接口的实际行为。
// 业务里用字符串或数字数组表示的有限取值，在文档里写成枚举、定长数组和范围，
// 前端据此生成更准确的类型和校验规则。
// Tighten the wire contract where CLR strings/arrays encode a finite domain or tuple.
public sealed class ContractSchemaTransformer : IOpenApiSchemaTransformer
{
    // 每描述一个类型就调用一次；这里按类型名补上枚举、长度和取值范围。
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        // context 里带着当前正在描述的类型，用它判断要不要特殊处理。
        var type = context.JsonTypeInfo.Type;
        // 上传文件在文档里要写成二进制字符串，而不是普通对象。
        if (type == typeof(IFormFile))
        {
            // 这样生成的 SDK 会用文件上传的方式发请求，浏览器才认得。
            schema.Type = JsonSchemaType.String; schema.Format = "binary";
            schema.Properties = null; schema.Required = null; schema.AdditionalPropertiesAllowed = false;
        }
        // 只处理本项目自己的类型：应用 DTO、领域值对象和 API 响应。
        if ((type.Assembly == typeof(JobView).Assembly || type.Assembly == typeof(Pitch).Assembly ||
             type.Namespace == typeof(AnnotationsView).Namespace) && schema.Properties is { } props)
        {
            // 字段默认全部必填：后端返回时不会漏字段，前端就能生成非可选属性。
            schema.Required = props.Keys.ToHashSet();
            // Pitch 去掉由音高算出来的 midi、diatonic，避免前端拿到两份可能不一致的数字。
            if (type == typeof(Pitch))
            {
                // alter 是变音记号数：-2 重降到 2 重升，0 表示不加记号；octave 是八度编号。
                foreach (var name in new[] { "midi", "diatonic" }) { props.Remove(name); schema.Required.Remove(name); }
                Range("alter", -2, 2); Range("octave", 0, 9);
            }
            // 任务状态只可能是这几个词，列成枚举后前端能生成固定选项。
            if (type == typeof(JobView) || type == typeof(PageJobView)) Enum("state", "Queued", "Processing", "Cancelling", "Cancelled", "Ready", "Failed", "Partial");
            // 修订和导出的状态更少，没有取消和重试状态。
            if (type == typeof(RevisionView) || type == typeof(DocumentExportView)) Enum("state", "Queued", "Processing", "Ready", "Failed");
            // 页任务额外带导出状态，单独约束一次。
            if (type == typeof(PageJobView)) Enum("exportState", "Queued", "Processing", "Ready", "Failed");
            if (type == typeof(AnnotationView))
            {
                Enum("status", "placed", "needs-manual-placement", "hidden");
                Length("baselineOffset", 2); Length("bounds", 4); Length("labelBounds", 4);
                // dots 里每个点是 [圆心x, 圆心y, 半径]，所以内层数组固定 3 个数。
                if (props["dots"] is OpenApiSchema dots && dots.Items is OpenApiSchema dot) dot.MinItems = dot.MaxItems = 3;
            }
            // 裁剪框是 [左上x, 左上y, 宽, 高] 四个数。
            if (type == typeof(PdfGeometryView)) Length("cropBox", 4);
            // 样式里字号是 3–16 磅，颜色是 #rrggbb 七位。
            if (type == typeof(AnnotationStyle))
            {
                Range("fontSizePoints", 3, 16);
                if (props["color"] is OpenApiSchema color) { color.Pattern = "^#[0-9a-fA-F]{6}$"; color.MinLength = color.MaxLength = 7; }
            }
            // 版本、页数、小节号这类序号从 1 开始，最小值为 1。
            foreach (var name in new[] { "version", "expectedRevision", "pageCount", "number", "startMeasure" }) Range(name, 1, null);
            // 尝试次数和当前修订从 0 开始，所以最小值是 0。
            foreach (var name in new[] { "attempts", "currentRevision" }) Range(name, 0, null);
            // 局部小工具：按字段名设置最小值/最大值，字段不存在就跳过。
            void Range(string name, int min, int? max) { if (props.TryGetValue(name, out var p) && p is OpenApiSchema value) { value.Minimum = min.ToString(); value.Maximum = max?.ToString(); } }
            // 局部小工具：按字段名写枚举；字段允许 null 时把 null 也加进去。
            void Enum(string name, params string[] values) { if (props.TryGetValue(name, out var p) && p is OpenApiSchema value) { value.Enum = values.Select(x => (JsonNode)JsonValue.Create(x)!).ToList(); if (value.Type?.HasFlag(JsonSchemaType.Null) == true) value.Enum.Add(null!); } }
            // 局部小工具：把数组字段写成固定长度。
            void Length(string name, int length) { if (props.TryGetValue(name, out var p) && p is OpenApiSchema value) value.MinItems = value.MaxItems = length; }
        }
        return Task.CompletedTask;
    }
}
