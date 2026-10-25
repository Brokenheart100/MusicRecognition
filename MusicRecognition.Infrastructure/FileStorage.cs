using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;

namespace MusicRecognition.Infrastructure;

public sealed class RecognitionOptions
{
    public string RepositoryRoot { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public string Python { get; set; } = "python";
    public string Pdftoppm { get; set; } = "";
    public string PdftoppmPath => string.IsNullOrWhiteSpace(Pdftoppm)
        ? Path.Combine(RepositoryRoot, ".tools", "poppler", "bin", OperatingSystem.IsWindows() ? "pdftoppm.exe" : "pdftoppm")
        : Path.GetFullPath(Pdftoppm, RepositoryRoot);
    public string Font { get; set; } = "C:/Windows/Fonts/seguisym.ttf";
    public int TimeoutSeconds { get; set; } = 300;
    public int WorkerCount { get; set; } = 1;
    public int MinimumFreeSpaceMiB { get; set; } = 512;
    public bool RequireOpenCv { get; set; }
    public bool RequireComposition { get; set; }
}
public sealed class FileStorage(RecognitionOptions options, ILogger<FileStorage> logger) : IScoreStorage
{
    public string JobDirectory(Guid id) => Path.Combine(Path.GetFullPath(options.DataRoot), id.ToString("N"));
    public void DiscardSource(Guid id, string extension)
    {
        logger.LogInformation("🧹 清理重复上传的源文件（任务 {ScoreId}，格式 {Extension}）", id, extension);
        File.Delete(Path.Combine(JobDirectory(id), "source" + extension));
    }
    public async Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct)
    {
        var name = ScoreDiagnostics.SafeFileName(filename);
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["ScoreId"] = id, ["FileName"] = name });
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (ext == ".jpeg") ext = ".jpg";
        if (ext is not (".pdf" or ".jpg" or ".png"))
        {
            logger.LogWarning("🚫 {FileName}：上传被拒绝，不支持格式 {Extension}（任务 {ScoreId}）", name, ext, id);
            throw new RequestFailure(415, "unsupported-type", "Only PDF (1–30 pages), JPG and PNG files are supported.");
        }
        var dir = JobDirectory(id); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "source" + ext);
        try
        {
            long length = 0;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920]; int count;
                while ((count = await stream.ReadAsync(buffer, ct)) != 0)
                {
                    length += count;
                    if (length > 20 * 1024 * 1024)
                    {
                        logger.LogWarning("🚫 {FileName}：上传被拒绝，文件 {Length} 字节超过 20 MiB 上限（任务 {ScoreId}）", name, length, id);
                        throw new RequestFailure(413, "file-too-large", "Maximum file size is 20 MiB.");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (length < 8)
                {
                    logger.LogWarning("🚫 {FileName}：上传被拒绝，文件为空或不完整，仅 {Length} 字节（任务 {ScoreId}）", name, length, id);
                    throw new RequestFailure(400, "invalid-file", "File is empty or truncated.");
                }
            }
            var bytes = new byte[8];
            await using var input = File.OpenRead(path); await input.ReadExactlyAsync(bytes, ct);
            var valid = ext switch
            {
                ".pdf" => bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
                ".png" => bytes.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                _ => bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
            };
            if (!valid)
            {
                logger.LogWarning("🚫 {FileName}：上传被拒绝，文件内容与扩展名 {Extension} 不符（任务 {ScoreId}）", name, ext, id);
                throw new RequestFailure(400, "invalid-signature", "File signature does not match its extension.");
            }
            input.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            logger.LogInformation("💾 {FileName}：上传已保存，格式 {Extension}，大小 {SizeKb:F0} KiB，文件摘要 {HashPrefix}（任务 {ScoreId}）", name, ext, length / 1024.0, hash[..12], id);
            return (ext, hash);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "🚫 {FileName}：上传失败，格式 {Extension}，错误码 {ErrorCode}，原因 {FailureReason}（任务 {ScoreId}）",
                name, ext, e is RequestFailure failure ? failure.Code : e is OperationCanceledException ? "upload-cancelled" : "source-storage-failed", e.Message, id);
            try { File.Delete(path); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { logger.LogWarning(cleanup, "{FileName}：上传临时文件清理失败，保留原始异常（任务 {ScoreId}）", name, id); }
            throw;
        }
    }
}
