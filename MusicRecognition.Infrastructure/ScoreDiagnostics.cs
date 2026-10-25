using MusicRecognition.Application;

namespace MusicRecognition.Infrastructure;

// Display names are diagnostic metadata, never filesystem paths or task identities.
public sealed record ScoreDiagnostics(Guid ScoreId, Guid DocumentId, string FileName, int PageNumber, string ScoreName)
{
    public static string SafeFileName(string filename)
    {
        var name = filename.Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(name) ? "未命名乐谱" : name[..Math.Min(name.Length, 200)];
    }

    public static async Task<ScoreDiagnostics> ResolveAsync(IScoreRepository repository, ScoreJob job, CancellationToken ct)
    {
        var parent = job.ParentId is { } parentId ? await repository.FindJobAsync(parentId, ct) : null;
        var filename = SafeFileName(parent?.FileName ?? job.FileName);
        var display = job.Extension==FragmentService.Extension ? $"{filename} - 片段拼谱" : job.ParentId is not null || job.PagesPrepared && job.PageCount == 1
            ? $"{filename} - 第{job.PageNumber}页" : $"{filename} - 整份文档";
        return new(job.Id, job.ParentId ?? job.Id, filename, job.PageNumber, display);
    }

    public Dictionary<string, object?> Scope() => new()
    {
        ["ScoreId"] = ScoreId, ["DocumentId"] = DocumentId, ["FileName"] = FileName,
        ["PageNumber"] = PageNumber, ["ScoreName"] = ScoreName
    };

    public static string Operation(string value) => value switch
    {
        "prepare" => "PDF 拆页", "recognize" => "识谱", "render" => "标注导出",
        "merge" => "整份 PDF 合并", "reflow" => "辅助谱重排", "compose" => "片段拼谱", _ => value
    };
}
