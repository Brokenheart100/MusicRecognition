namespace MusicRecognition.Infrastructure.Configuration;

public sealed class RecognitionOptions
{
    public string RepositoryRoot { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public string Python { get; set; } = "python";
    public string HomrPython { get; set; } = "";
    public string HomrPythonPath => string.IsNullOrWhiteSpace(HomrPython)
        ? Path.Combine(RepositoryRoot, ".tools", "homr", "venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python")
        : Path.GetFullPath(HomrPython, RepositoryRoot);
    public string Pdftoppm { get; set; } = "";
    public string PdftoppmPath => string.IsNullOrWhiteSpace(Pdftoppm)
        ? Path.Combine(RepositoryRoot, ".tools", "poppler", "bin", OperatingSystem.IsWindows() ? "pdftoppm.exe" : "pdftoppm")
        : Path.GetFullPath(Pdftoppm, RepositoryRoot);
    public string Font { get; set; } = "C:/Windows/Fonts/seguisym.ttf";
    public int TimeoutSeconds { get; set; } = 300;
    public int WorkerCount { get; set; } = 1;
    public int MinimumFreeSpaceMiB { get; set; } = 512;
    public bool RequireOpenCv { get; set; }
}
