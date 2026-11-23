using Aspire.Hosting.Docker.Resources.ComposeNodes;

// 这个文件是整套系统的“总开关”：决定 API、Worker、网页和数据库怎么一起启动。
// Aspire 是 .NET 的编排工具，可以理解成一张启动清单，按顺序拉起每个服务并配好连接。
// 发布到服务器时也走这里：换成 Docker Compose 的容器版本。
// 所以下面会先判断当前是“本机开发”还是“打包发布”，两条分支各有一套资源。
var builder = DistributedApplication.CreateBuilder(args);
// publishing 为 true 表示正在生成部署配置（容器），false 是日常本机开发。
var publishing = builder.ExecutionContext.IsPublishMode;
// 发布时给监控仪表盘生成 64 位随机密钥，避免面板被人随便打开。
var dashboardKey = publishing ? builder.AddParameter("dashboard-api-key",
    new GenerateParameterDefault { MinLength = 64, Lower = true, Upper = true, Numeric = true, Special = false }, secret: true) : null;
// 注册 Docker Compose 环境，并配置 Aspire 自带的监控仪表盘（看日志、追踪和指标）。
var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard =>
    {
        // 镜像用 SHA256 固定版本，保证每次启动的是同一份可信镜像。
        dashboard.WithImageSHA256("cf82f4314b4b3e94285f9b6aa2141fd484377fd926df0ef11cf620e43bc39ba8").PublishAsDockerComposeService((_, service) =>
        {
            // 仪表盘只绑定本机回环地址 127.0.0.1:18888，外网访问不到。
            service.Ports.Clear(); service.Ports.Add("127.0.0.1:18888:18888");
            // 异常退出时自动重启；限制 1 核 CPU、512 MB 内存，避免抢走识别资源。
            service.Restart = "unless-stopped";
            service.Deploy = new() { Resources = new() { Limits = new() { Cpus = "1", Memory = "512m" } } };
        });
        // 有密钥时启用 API Key 鉴权，密钥同时用于前端浏览器登录。
        if (dashboardKey is not null)
            dashboard.WithEnvironment("Dashboard__Api__AuthMode", "ApiKey").WithEnvironment("Dashboard__Api__PrimaryApiKey", dashboardKey)
                .WithEnvironment("Dashboard__Frontend__AuthMode", "BrowserToken").WithEnvironment("Dashboard__Frontend__BrowserToken", dashboardKey);
    });

// PostgreSQL 是存乐谱任务、修订和任务状态的数据库。
var postgres = builder.AddPostgres("recognition-postgres", port: 5432)
    // 数据库文件放在 Docker 数据卷里；部署和开发用不同卷名，避免两套数据混在一起。
    .WithDataVolume(publishing ? "musicrecognition-deployed-postgres" : "musicrecognition-postgres-data");
// 在数据库实例里建 recognitiondb 库，之后所有表都放这里。
var database = postgres.AddDatabase("recognitiondb");

// root 是仓库根目录：后面按相对路径找 Dockerfile、Python 环境和输出目录。
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
// 发布模式：全部用容器运行，由 Docker Compose 统一管理。
if (publishing)
{
    // API 容器：提供 HTTP 接口，构建目标是 backend.Dockerfile 的 api 阶段。
    var apiContainer = builder.AddDockerfile("api", root, "deploy/backend.Dockerfile", "api")
        .WithOtlpExporter()
        // 等数据库就绪再启动，并把数据库连接信息注入为环境变量。
        .WithReference(database).WaitFor(database).WithHttpEndpoint(targetPort: 8080, name: "http")
        // 识别产物放 /data 卷，API 和 Worker 共用，容器重启也不丢。
        .WithVolume("musicrecognition-deployed-scores", "/data")
        // 识别超时放宽到 900 秒（默认 300），大谱子需要更久。
        .WithEnvironment("Recognition__TimeoutSeconds", "900")
        // 限制 2 核 CPU、1 GB 内存，并开启末尾 Harden 定义的安全加固。
        .PublishAsDockerComposeService((_, service) => Harden(service, "2", "1g", false));
    // Worker 容器：同一份后端镜像的 worker 阶段，只跑后台任务。
    builder.AddDockerfile("worker", root, "deploy/backend.Dockerfile", "worker")
        .WithOtlpExporter()
        // 先等数据库和 API 就绪，避免任务抢跑或连不上接口。
        .WithReference(database).WaitFor(database).WaitFor(apiContainer).WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithVolume("musicrecognition-deployed-scores", "/data")
        .WithEnvironment("Recognition__TimeoutSeconds", "900")
        // Worker 要跑识别和导出，内存给到 4 GB。
        .PublishAsDockerComposeService((_, service) => Harden(service, "2", "4g", false));
    // 网页容器：Nuxt 前端，用 deploy/web.Dockerfile 构建。
    builder.AddDockerfile("web", root, "deploy/web.Dockerfile")
        // 对外端口 3080，容器内 Nuxt 监听 3000；只绑本机，适合放在反向代理后面。
        .WithHttpEndpoint(port: 3080, targetPort: 3000, name: "http")
        // 把 API 地址和 API 的就绪状态传给前端：接口可用后再启动网页。
        .WithEnvironment("NUXT_API_BASE", apiContainer.GetEndpoint("http")).WaitFor(apiContainer)
        // 网页进程带 Node，按“外部服务”方式加固（见 Harden 的 external 参数）。
        .PublishAsDockerComposeService((_, service) => Harden(service, "1", "512m", true));
    // 数据库容器同样做加固：不对宿主机开放 5432，只有内部网络能连。
    postgres.PublishAsDockerComposeService((_, service) =>
    {
        // 不映射端口；异常自动重启。
        service.Ports.Clear(); service.Restart = "unless-stopped";
        service.Deploy = new() { Resources = new() { Limits = new() { Cpus = "1", Memory = "512m" } } };
        // 日志最多留 3 个 10 MB 文件，防止日志把磁盘占满。
        service.Logging = new() { Driver = "json-file", Options = { ["max-size"] = "10m", ["max-file"] = "3" } };
        // 用 pg_isready 探活：5 秒一次、3 秒超时、启动后 15 秒才开始判定。
        service.Healthcheck = new() { Test = ["CMD-SHELL", "pg_isready -U postgres"], Interval = "5s", Timeout = "3s", StartPeriod = "15s", Retries = 20 };
    });
    // 发布分支收尾：生成容器配置并启动；return 之后不再执行本机开发分支。
    builder.Build().Run();
    return;
}
// 本地开发模式：先确定 Python 解释器路径。
var projectPython = Path.Combine(root, ".tools", "poc-python", OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
// 优先级：显式配置 > 环境变量 MUSICRECOGNITION_PYTHON > 项目自带环境。
var python = builder.Configuration["Recognition:Python"] ?? Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? projectPython;
// 自带环境不存在就报错，提示先跑准备脚本，避免启动后才出现难懂的失败。
if (python == projectPython && !File.Exists(projectPython))
    throw new InvalidOperationException("Prepare the project Python environment with scripts/setup/setup.ps1, or explicitly configure Recognition:Python / MUSICRECOGNITION_PYTHON.");

// API 项目：本机直接运行 .NET 进程，连数据库并监听 HTTP。
var api = builder.AddProject<Projects.MusicRecognition_Api>("api")
    .WithReference(database).WaitFor(database)
    .WithHttpEndpoint(name: "http")
    // 健康检查打 /health；Aspire 靠它判断 API 是否可用。
    .WithHttpHealthCheck("/health")
    // 告诉后端仓库根在哪，识别工具和脚本按相对路径查找。
    .WithEnvironment("Recognition__RepositoryRoot", root)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName)
    // 识别产物落到 output/backend，开发时方便直接查看。
    .WithEnvironment("Recognition__DataRoot", Path.Combine(root, "output", "backend"))
    // 指定 Python 解释器，识别脚本用它运行。
    .WithEnvironment("Recognition__Python", python);

// Worker 项目：同样本机运行，与 API 共用数据库和文件。
builder.AddProject<Projects.MusicRecognition_Worker>("worker")
    // 先等数据库和 API 就绪，再开始消费任务。
    .WithReference(database).WaitFor(database).WaitFor(api)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health")
    // 仓库根、环境和输出目录与 API 保持一致，两边读写同一批文件。
    .WithEnvironment("Recognition__RepositoryRoot", root)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithEnvironment("Recognition__DataRoot", Path.Combine(root, "output", "backend"))
    // Python 路径也一致，Worker 真正调用识别脚本。
    .WithEnvironment("Recognition__Python", python);

// 前端 Vite/Nuxt 开发服务器：由 Aspire 一起启动并分配端口。
builder.AddViteApp("web", "../MusicRecognition.Web")
    // 网页健康检查，确认前端已经能响应。
    .WithHttpHealthCheck("/health")
    .WithHttpEndpoint(3000)
    // 把本机 API 地址传给前端，浏览器才能调到接口。
    .WithEnvironment("NUXT_API_BASE", api.GetEndpoint("http"))
    // 等 API 就绪后再启动网页，打开页面就能用。
    .WaitFor(api);

// 本机开发收尾：启动全部进程并保持运行。
builder.Build().Run();

// Harden 是发布模式的加固函数，给一个容器加上安全、资源和探活配置。
static void Harden(Service service, string cpus, string memory, bool external)
{
    service.Restart = "unless-stopped";
    // init 让容器里有个小的 1 号进程，负责回收僵尸进程。
    service.Init = true;
    // 丢掉所有 Linux capability，进程没有特权，被攻破也难做坏事。
    service.CapDrop.Add("ALL");
    // 根文件系统只读，程序不能改镜像里的文件。
    service.ReadOnly = true;
    // 唯一可写的是内存盘 /tmp（512 MB），临时文件重启即消失。
    service.Tmpfs.Add("/tmp:rw,nosuid,nodev,exec,size=512m,mode=1777");
    // 后端进程还要写 /home/app（128 MB），属主是镜像里的 1654 用户。
    if (!external) service.Tmpfs.Add("/home/app:rw,nosuid,nodev,exec,size=128m,uid=1654,gid=1654,mode=0700");
    // 禁止进程通过 setuid 等方式提权。
    service.SecurityOpt.Add("no-new-privileges:true");
    // 停止时给 30 秒优雅退出，等正在跑的识别任务收尾。
    service.StopGracePeriod = "30s";
    // 日志轮转，避免容器日志无限增长。
    service.Logging = new() { Driver = "json-file", Options = { ["max-size"] = "10m", ["max-file"] = "3" } };
    // CPU 和内存上限，防止一个服务拖垮整台机器。
    service.Deploy = new() { Resources = new() { Limits = new() { Cpus = cpus, Memory = memory } } };
    // 健康检查：15 秒一次、5 秒超时、启动后 90 秒开始判定、连续 5 次失败算不健康。
    service.Healthcheck = new()
    {
        Interval = "15s",
        Timeout = "5s",
        StartPeriod = "90s",
        Retries = 5,
        Test = external
            ? ["CMD", "node", "-e", "fetch('http://127.0.0.1:3000/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"]
            // 后端的 python 在镜像固定路径，用标准库 urllib 打 8080 的 /health。
            : ["CMD", "/opt/recognition/.tools/poc-python/bin/python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health',timeout=4)"]
    };
    service.Ports.Clear();
    // 只有网页需要被访问，绑定在本机 3080，再由反向代理对外。
    if (external) service.Ports.Add("127.0.0.1:3080:3000");
}
