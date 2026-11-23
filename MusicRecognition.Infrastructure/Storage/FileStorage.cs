using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Infrastructure.Configuration;
using MusicRecognition.Infrastructure.Observability;

namespace MusicRecognition.Infrastructure.Storage;

// 文件存储：用户上传的原谱落到数据盘，每份乐谱一个独立目录。
// 除了保存，还负责三道关：扩展名白名单、大小上限、文件头（魔数）校验。
// 写入时顺带算出 SHA-256 摘要，作为“这是不是同一份文件”的去重依据。
public sealed class FileStorage(RecognitionOptions options, ILogger<FileStorage> logger) : IScoreStorage
{
    // 每份乐谱一个目录，用任务 ID 的 32 位短格式命名，避免路径过长。
    public string JobDirectory(Guid id) => Path.Combine(Path.GetFullPath(options.DataRoot), id.ToString("N"));
    // 识别到重复上传时，把刚写下的源文件删掉，不占磁盘。
    public void DiscardSource(Guid id, string extension)
    {
        logger.LogInformation("🧹 清理重复上传的源文件（任务 {ScoreId}，格式 {Extension}）", id, extension);
        File.Delete(Path.Combine(JobDirectory(id), "source" + extension));
    }
    // 保存上传：校验格式、大小和文件头，写入任务目录，返回扩展名和 SHA-256 摘要。
    // 摘要用于去重：同一份文件再次上传时不会重复识谱。
    public async Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct)
    {
        // 文件名只用于日志展示，先洗掉路径分隔符等危险字符，防止日志注入。
        var name = ScoreDiagnostics.SafeFileName(filename);
        // 日志作用域带上任务 ID 和文件名，排查上传问题能直接按关键词搜。
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["ScoreId"] = id, ["FileName"] = name });
        // 扩展名统一小写；.jpeg 归并成 .jpg，后面只认三种格式。
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (ext == ".jpeg") ext = ".jpg";
        // 只收 PDF（1–30 页）、JPG、PNG；其它格式直接返回 415。
        if (ext is not (".pdf" or ".jpg" or ".png"))
        {
            logger.LogWarning("🚫 {FileName}：上传被拒绝，不支持格式 {Extension}（任务 {ScoreId}）", name, ext, id);
            throw new RequestFailure(415, "unsupported-type", "Only PDF (1–30 pages), JPG and PNG files are supported.");
        }
        // 任务目录第一次使用时创建。
        var dir = JobDirectory(id); Directory.CreateDirectory(dir);
        // 磁盘上的文件名固定成 source + 扩展名，用户原始文件名不进路径。
        var path = Path.Combine(dir, "source" + ext);
        try
        {
            long length = 0;
            // FileMode.CreateNew：同名文件已存在就报错，从根上避免覆盖别人的上传。
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                // 边读边写，缓冲区 81920 字节（80 KiB），大文件也不用整个读进内存。
                var buffer = new byte[81920]; int count;
                while ((count = await stream.ReadAsync(buffer, ct)) != 0)
                {
                    length += count;
                    // 20 MiB 是单文件上限，够手机拍的照片和常见乐谱 PDF 用。
                    if (length > 20 * 1024 * 1024)
                    {
                        logger.LogWarning("🚫 {FileName}：上传被拒绝，文件 {Length} 字节超过 20 MiB 上限（任务 {ScoreId}）", name, length, id);
                        throw new RequestFailure(413, "file-too-large", "Maximum file size is 20 MiB.");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                // 小于 8 字节不可能是有效文件，直接拒绝。
                if (length < 8)
                {
                    logger.LogWarning("🚫 {FileName}：上传被拒绝，文件为空或不完整，仅 {Length} 字节（任务 {ScoreId}）", name, length, id);
                    throw new RequestFailure(400, "invalid-file", "File is empty or truncated.");
                }
            }
            // 读文件头 8 个字节做“魔数”校验，挡住改了扩展名的假文件。
            var bytes = new byte[8];
            await using var input = File.OpenRead(path); await input.ReadExactlyAsync(bytes, ct);
            // PDF 头是 %PDF- 五个字节；PNG 是固定 8 字节；JPG 以 FF D8 FF 开头。
            var valid = ext switch
            {
                ".pdf" => bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
                ".png" => bytes.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                _ => bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
            };
            // 文件头和扩展名对不上就拒绝，避免后续工具拿到坏文件崩掉。
            if (!valid)
            {
                logger.LogWarning("🚫 {FileName}：上传被拒绝，文件内容与扩展名 {Extension} 不符（任务 {ScoreId}）", name, ext, id);
                throw new RequestFailure(400, "invalid-signature", "File signature does not match its extension.");
            }
            input.Position = 0;
            // SHA-256 摘要转成大写十六进制字符串，作为去重键和日志里的短标识。
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            logger.LogInformation("💾 {FileName}：上传已保存，格式 {Extension}，大小 {SizeKb:F0} KiB，文件摘要 {HashPrefix}（任务 {ScoreId}）", name, ext, length / 1024.0, hash[..12], id);
            return (ext, hash);
        }
        // 任何一步失败都删掉写了一半的文件，不留半成品给后续流程误用。
        catch (Exception e)
        {
            // 失败原因分三类记录：上传取消、请求校验不过、磁盘写入失败。
            logger.LogWarning(e, "🚫 {FileName}：上传失败，格式 {Extension}，错误码 {ErrorCode}，原因 {FailureReason}（任务 {ScoreId}）",
                name, ext, e is RequestFailure failure ? failure.Code : e is OperationCanceledException ? "upload-cancelled" : "source-storage-failed", e.Message, id);
            // 删除本身也可能失败（比如被杀毒软件占用），只警告，不覆盖原始异常。
            try { File.Delete(path); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { logger.LogWarning(cleanup, "{FileName}：上传临时文件清理失败，保留原始异常（任务 {ScoreId}）", name, id); }
            throw;
        }
    }
}
