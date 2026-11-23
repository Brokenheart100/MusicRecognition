using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicRecognition.Application.Handlers;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Infrastructure.BackgroundJobs;
using MusicRecognition.Infrastructure.Configuration;
using MusicRecognition.Infrastructure.HealthChecks;
using MusicRecognition.Infrastructure.Persistence.Repositories;
using MusicRecognition.Infrastructure.Recognition;
using MusicRecognition.Infrastructure.Storage;

namespace MusicRecognition.Infrastructure;

// 基础设施层的“装配说明书”：把配置、数据库、文件存储、识谱引擎和后台任务注册进依赖注入容器。
// 可以理解为开店前的准备工作——先把人、货、设备登记到花名册，后面谁要用直接按名字取。
// API 和服务 Worker 共用这里；worker=true 时额外加上后台任务相关的组件。
public static class DependencyInjection
{
    // 注册识谱相关的全部服务；worker 参数区分“只提供接口的 API”和“真正干活的 Worker”。
    public static IServiceCollection AddRecognition(this IServiceCollection services, IConfiguration config, bool worker)
    {
        // 从配置的 Recognition 节读出参数；没有配置节时用空对象，靠下面校验兜底。
        var options = config.GetSection("Recognition").Get<RecognitionOptions>() ?? new();
        // 启动时就把明显写错的配置挡下：路径必填，超时 10~1800 秒，并发 1~4，空间上限 1 TiB。
        if (string.IsNullOrWhiteSpace(options.DataRoot) || string.IsNullOrWhiteSpace(options.RepositoryRoot) || options.TimeoutSeconds is < 10 or > 1800 || options.WorkerCount is < 1 or > 4 || options.MinimumFreeSpaceMiB is < 0 or > 1048576)
            throw new InvalidOperationException("Configure Recognition RepositoryRoot, DataRoot and TimeoutSeconds (10..1800).");
        // 配置和文件存储整个进程只放一份；其余按请求或任务创建，避免共享数据库上下文。
        services.AddSingleton(options); services.AddSingleton<IScoreStorage, FileStorage>();
        // 连接串缺失就直接启动失败，不要等第一次请求才发现连不上数据库。
        var connection = config.GetConnectionString("recognitiondb") ?? throw new InvalidOperationException("Missing recognitiondb connection string.");
        services.AddDbContext<ScoreDb>(o => o.UseNpgsql(connection));
        // 注册两个健康检查：数据库能否连上、谱面存储目录能否写入。
        services.AddHealthChecks().AddCheck<DatabaseHealth>("recognitiondb").AddCheck<DataStorageHealth>("score-storage");
        // 仓储、用例服务和识别引擎都按作用域注册；ToolEngine 同时以具体类型注册，供内部直接取用。
        services.AddScoped<IScoreRepository, ScoreRepository>(); services.AddScoped<IScoreService, ScoreService>(); services.AddScoped<IRecognitionEngine, ToolEngine>(); services.AddScoped<ScoreProcessor>();
        services.AddScoped<ToolEngine>();
        // Hangfire 用同一个数据库存后台任务：任务被取走后隐藏 1 分钟（防重复领取），每 2 秒查一次新任务。
        services.AddHangfire(h => h.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connection), new PostgreSqlStorageOptions { InvisibilityTimeout = TimeSpan.FromMinutes(1), QueuePollInterval = TimeSpan.FromSeconds(2) }));
        // 只有 Worker 进程才需要执行任务：注册运行环境检查、Hangfire 执行器和调度服务。
        if (worker)
        {
            // 启动时检查 Python、PDF 工具等依赖，并把检查结果暴露成健康检查。
            services.AddSingleton<RecognitionRuntime>();
            services.AddHealthChecks().AddCheck<RecognitionRuntime>("recognition-runtime");
            // 两个队列分工：recognition 跑识谱，render 跑导出；每秒检查一次取消请求。
            services.AddHangfireServer(o => { o.WorkerCount = options.WorkerCount; o.Queues = ["recognition", "render"]; o.CancellationCheckInterval = TimeSpan.FromSeconds(1); o.ShutdownTimeout = TimeSpan.FromSeconds(10); });
            // 调度服务负责把数据库里“排队中”的任务真正投递给 Hangfire。
            services.AddHostedService<DispatchService>();
        }
        return services;
    }
    // 启动时执行数据库迁移，把表结构升到最新版本（没有变化时什么也不做）。
    public static async Task MigrateRecognitionAsync(this IServiceProvider provider, CancellationToken ct = default)
    { await using var scope = provider.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.MigrateAsync(ct); }
}
