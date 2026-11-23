using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using MusicRecognition.Api.Endpoints;
using MusicRecognition.Api.Extensions;
using MusicRecognition.Api.Middleware;
using MusicRecognition.Api.OpenApi;
using MusicRecognition.Infrastructure;
using MusicRecognition.ServiceDefaults;

// API 的启动入口（组合根）：把配置、数据库、限流、文档和路由按顺序装好。
// 可以当成一份装配清单：先注册依赖，再排中间件，最后启动。
// 顺序会影响请求处理，越先注册的中间件越早接触请求。
var builder = WebApplication.CreateBuilder(args);
// 接入日志、指标和健康检查，让运维能观察这个服务。
builder.AddServiceDefaults();
// 注册后端共用组件；worker:false 表示本进程只接 HTTP，不跑后台识别任务。
builder.Services.AddRecognition(builder.Configuration, worker: false);
// 统一 JSON 规则：下面两项决定枚举和数字怎么出现在接口里。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // 枚举输出名字而不是数字；数字字段严格校验，不接受字符串。
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
// 上传上限 21 MiB（21×1024×1024 字节）；表单解析和 HTTP 服务器两处都要放宽。
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 21 * 1024 * 1024);
// Kestrel 是 HTTP 服务器，它自己也有一道请求体大小限制，需要同步放宽。
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 21 * 1024 * 1024);
// 统一错误响应格式（ProblemDetails），配合下面的错误中间件使用。
builder.Services.AddProblemDetails();
// 启用写请求限流：上传、校对、导出这类会跑识别引擎的操作受配额限制。
builder.Services.AddRequestProtection(builder.Configuration);
// 生成 OpenAPI 文档，并挂两个转换器让文档与真实接口一致。
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<ContractSchemaTransformer>();
    options.AddOperationTransformer<ContractOperationTransformer>();
});

// 开始装配请求管线，中间件按下面的先后顺序生效。
var app = builder.Build();
// 最外层先加安全响应头，后面出错时响应也会带上这些头。
app.UseResponseHeaders();
// 接着统一错误格式，把异常换成 ProblemDetails。
app.UseApiErrorHandling();
// 再限流，超出配额的写请求在这里被拒。
app.UseRateLimiter();
// 健康检查：/health 和 /alive 供 Aspire 探活。
app.MapDefaultEndpoints();
// 只有开发环境才暴露 OpenAPI 文档，生产环境不公开。
if (app.Environment.IsDevelopment()) app.MapOpenApi();
// 挂上 /api/scores 下的全部乐谱接口。
app.MapScoreEndpoints();
// 启动前先跑数据库迁移，保证表结构是最新版本。
await app.Services.MigrateRecognitionAsync();
// 开始接收请求，进程会一直运行到被停止。
app.Run();
