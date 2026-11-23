using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MusicRecognition.Infrastructure.Persistence.DesignTime;

// 设计时工厂：给 `dotnet ef` 命令（生成迁移、更新模型快照）使用。
// 迁移工具不会启动整个应用，所以需要这个类自己造一个不依赖运行配置的数据库上下文。
// 连接串只用于让工具识别数据库类型，迁移生成阶段并不会真的连库改数据。
public sealed class ScoreDbFactory : IDesignTimeDbContextFactory<ScoreDb>
{
    // 优先读环境变量 ConnectionStrings__recognitiondb；没有就用本地开发用的占位连接串。
    // 占位密码只为让 EF 认出这是 PostgreSQL，真实迁移时请改用环境变量。
    public ScoreDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ScoreDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__recognitiondb") ??
            "Host=localhost;Database=recognitiondb;Username=postgres;Password=design-time-only").Options);
}
