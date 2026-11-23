using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MusicRecognition.Api.OpenApi;

// 这个文件只调整 OpenAPI 文档，不改接口的真实行为。
// 前端 SDK 按这份文档自动生成，枚举、长度和文件类型写准了才不会对不上。
// 可以理解为给接口补“说明书”，前端照着它生成调用代码。
internal sealed class ContractOperationTransformer : IOpenApiOperationTransformer
{
    // 按接口名逐项修正文档：请求头长度、表单下拉可选值、下载文件类型。
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        // 逐个检查接口参数，只处理放在请求头里的文本参数。
        foreach (var parameter in operation.Parameters ?? [])
            if (parameter.In == ParameterLocation.Header && parameter.Schema is OpenApiSchema header)
            {
                // 访问令牌限 32–200 字符、幂等键限 8–100 字符，和后端校验保持一致。
                if (parameter.Name == "X-Score-Token") { header.MinLength = 32; header.MaxLength = 200; }
                if (parameter.Name == "Idempotency-Key") { header.MinLength = 8; header.MaxLength = 100; }
            }
        // TypedResults.Accepted 在运行时写入 Location；补进文档，生成客户端才知道轮询地址。
        foreach (var response in operation.Responses ?? [])
            if (response.Key == "202" && response.Value is OpenApiResponse accepted)
            {
                accepted.Headers ??= new Dictionary<string, IOpenApiHeader>();
                accepted.Headers["Location"] = new OpenApiHeader
                {
                    Description = "URI of the created resource.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }
                };
            }
        // 上传接口的 preprocessing 只接受这些值；大小写两种写法都列出来，因为后端两种都收。
        if (operation.OperationId == "ImportScore" && operation.RequestBody?.Content is { } multipart)
            foreach (var media in multipart.Values)
                if (media.Schema is OpenApiSchema form)
                    foreach (var part in form.AllOf ?? [])
                        if (part.Properties?.TryGetValue("preprocessing", out var property) == true && property is OpenApiSchema preprocessing)
                            preprocessing.Enum = new[] { "", "None", "Adaptive", "none", "adaptive" }.Select(x => (JsonNode)JsonValue.Create(x)!).ToList();
        // engine 也一样；它的字段可能挂在表单自身或 AllOf 组成项上，所以两处都查。
        if (operation.OperationId is "ImportScore" && operation.RequestBody?.Content is { } imports)
            foreach (var media in imports.Values)
                if (media.Schema is OpenApiSchema form)
                    foreach (var part in (form.AllOf ?? []).Append(form))
                        if (part.Properties?.TryGetValue("engine", out var property) == true && property is OpenApiSchema engine)
                            engine.Enum = new[] { "", "Audiveris", "Homr", "audiveris", "homr" }.Select(x => (JsonNode)JsonValue.Create(x)!).ToList();
        // 原谱和成品 PDF 是二进制下载，200 是完整文件，206 表示按范围返回（断点续传）。
        if (operation.OperationId is "GetScoreSource" or "GetAnnotatedPdf")
            SetBinaryResponse(operation, ["200", "206"], operation.OperationId == "GetAnnotatedPdf" ? "application/pdf" : "application/octet-stream");
        // MusicXML 用专门的类型，其他软件一看就知道这是乐谱文件。
        if (operation.OperationId is "GetScoreMusicXml")
            SetBinaryResponse(operation, ["200"], "application/vnd.recordare.musicxml");
        // 标注和指标内容虽是 JSON，但调用方是下载文件，所以按二进制流描述。
        if (operation.OperationId is "GetRevisionAnnotations" or "GetRevisionMetrics")
            SetBinaryResponse(operation, ["200"], "application/json");
        return Task.CompletedTask;
    }

    // 把指定状态码的响应改写成“二进制文件下载”，生成的前端代码会拿到字节流。
    private static void SetBinaryResponse(OpenApiOperation operation, string[] statuses, string contentType)
    {
        // 逐个状态码替换响应，Content-Type 用调用方传入的类型。
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
