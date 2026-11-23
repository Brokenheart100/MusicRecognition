namespace MusicRecognition.Infrastructure.Configuration;

// 识谱服务的全部可调参数，在配置文件里对应 Recognition 配置节。
// 路径分成两类：仓库根目录用来找随项目安装的外部工具，数据目录用来存用户上传的原谱和产物。
// 每个参数都有默认值，本地开发不配置也能先跑起来。
public sealed class RecognitionOptions
{
    // 代码仓库根目录；下面的工具路径都相对它拼出来，避免把绝对路径写死。
    public string RepositoryRoot { get; set; } = "";
    // 上传原谱、拆出的页面图片和导出结果的存放根目录，运行时必须可写。
    public string DataRoot { get; set; } = "";
    public string Python { get; set; } = "python";
    // homr 需要独立的 Python 环境；留空就从仓库的 .tools/homr/venv 取默认解释器。
    public string HomrPython { get; set; } = "";
    // 上面留空时按操作系统拼解释器路径：Windows 在 Scripts 下，其它系统在 bin 下。
    public string HomrPythonPath => string.IsNullOrWhiteSpace(HomrPython)
        ? Path.Combine(RepositoryRoot, ".tools", "homr", "venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python")
        : Path.GetFullPath(HomrPython, RepositoryRoot);
    // PDF 转图片工具；留空就用仓库 .tools/poppler 目录里自带的那一份。
    public string Pdftoppm { get; set; } = "";
    // 上面留空时同理，按操作系统拼出可执行文件的位置。
    public string PdftoppmPath => string.IsNullOrWhiteSpace(Pdftoppm)
        ? Path.Combine(RepositoryRoot, ".tools", "poppler", "bin", OperatingSystem.IsWindows() ? "pdftoppm.exe" : "pdftoppm")
        : Path.GetFullPath(Pdftoppm, RepositoryRoot);
    // 往谱面上写简谱数字用的字体，默认是 Windows 自带的音乐符号字体。
    public string Font { get; set; } = "C:/Windows/Fonts/seguisym.ttf";
    // 单个识谱或渲染进程最长运行秒数，默认 300 秒，超时会强制终止。
    public int TimeoutSeconds { get; set; } = 300;
    public int WorkerCount { get; set; } = 1;
    // 数据盘剩余空间低于这个值（MiB，默认 512）时，健康检查就报不健康。
    public int MinimumFreeSpaceMiB { get; set; } = 512;
    // 是否强制要求安装 OpenCV（自适应预处理会用到），缺了就不允许启动。
    public bool RequireOpenCv { get; set; }
}
