using System.Security.Cryptography;
using MusicRecognition.Application;

namespace MusicRecognition.Infrastructure;

public sealed class RecognitionOptions
{
    public string RepositoryRoot { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public string Python { get; set; } = "python";
    public string Font { get; set; } = "C:/Windows/Fonts/seguisym.ttf";
    public int TimeoutSeconds { get; set; } = 300;
}
public sealed class FileStorage(RecognitionOptions options) : IScoreStorage
{
    public string JobDirectory(Guid id) => Path.Combine(Path.GetFullPath(options.DataRoot), id.ToString("N"));
    public void DiscardSource(Guid id,string extension) => File.Delete(Path.Combine(JobDirectory(id),"source"+extension));
    public async Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (ext == ".jpeg") ext = ".jpg";
        if (ext is not (".pdf" or ".jpg" or ".png")) throw new RequestFailure(415, "unsupported-type", "Only single-page PDF, JPG and PNG are supported in P1.");
        var dir = JobDirectory(id); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "source" + ext);
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920]; long length = 0; int count;
                while ((count = await stream.ReadAsync(buffer, ct)) != 0)
                {
                    length += count;
                    if (length > 20 * 1024 * 1024) throw new RequestFailure(413, "file-too-large", "Maximum file size is 20 MiB.");
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (length < 8) throw new RequestFailure(400, "invalid-file", "File is empty or truncated.");
            }
            var bytes = new byte[8];
            await using var input = File.OpenRead(path); await input.ReadExactlyAsync(bytes, ct);
            var valid = ext switch { ".pdf" => bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
                ".png" => bytes.AsSpan().SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
                _ => bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 };
            if (!valid) throw new RequestFailure(400, "invalid-signature", "File signature does not match its extension.");
            input.Position = 0;
            return (ext, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)));
        }
        catch { File.Delete(path); throw; }
    }
}
