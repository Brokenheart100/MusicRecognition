using MusicRecognition.Infrastructure;
using MusicRecognition.Infrastructure.Recognition;
using MusicRecognition.ServiceDefaults;

// Worker 是后台进程：不直接接用户请求，只从队列里取识别和导出任务来跑。
// 它和 API 共用数据库、配置和文件目录，所以识别结果 API 能直接读到。
// 这里同样要接健康检查和数据库迁移，Aspire 才能判断它是否就绪。
var builder = WebApplication.CreateBuilder(args);
// 接入日志、指标和健康检查，与其他 .NET 服务保持一致。
builder.AddServiceDefaults();
// worker:true 会额外启动 Hangfire 后台任务执行器和任务派发。
builder.Services.AddRecognition(builder.Configuration, worker: true);
var app = builder.Build();
// 启动自检：Python、Audiveris 等识别工具是否就位；缺失就让进程报错退出。
await app.Services.GetRequiredService<RecognitionRuntime>().ValidateAsync(app.Lifetime.ApplicationStopping);
app.MapDefaultEndpoints();
// 启动前跑数据库迁移，与 API 使用同一版表结构。
await app.Services.MigrateRecognitionAsync();
app.Run();
