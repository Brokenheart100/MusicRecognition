using MusicRecognition.Application;
using MusicRecognition.Application.Interfaces;

namespace MusicRecognition.Infrastructure.Observability;

// 诊断上下文：给每条日志和每段追踪打上“这是哪份乐谱、第几页”的标签。
// 有了它，排查时按文件名或任务 ID 一搜，就能把一次识谱的日志串起来。
// 这里只放给用户看的展示名，不放服务器文件路径，避免泄漏本机目录结构。
// Display names are diagnostic metadata, never filesystem paths or task identities.
public sealed record ScoreDiagnostics(Guid ScoreId, Guid DocumentId, string FileName, int PageNumber, string ScoreName)
{
    // 把用户上传的原始文件名洗成安全显示名：只留文件名部分，控制字符换成空格，最长 200 字。
    // 这样日志里不会出现路径分隔符或换行，避免污染日志格式。
    public static string SafeFileName(string filename)
    {
        var name = filename.Replace('\\', '/').Split('/').Last();
        // 控制字符和格式字符（如零宽字符）会打乱日志行，统一换成空格。
        name = new string(name.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray()).Trim();
        // 空名字回退成“未命名乐谱”，最长截到 200 字，防止日志被超长文件名撑满。
        return string.IsNullOrWhiteSpace(name) ? "未命名乐谱" : name[..Math.Min(name.Length, 200)];
    }

    // 从任务记录和它的父任务推出日志里显示的名字：多页文档带“第 N 页”，单页或整份就是“整份文档”。
    public static async Task<ScoreDiagnostics> ResolveAsync(IScoreRepository repository, ScoreJob job, CancellationToken ct)
    {
        // 子任务自己不存原文件名，要向父任务要。
        var parent = job.ParentId is { } parentId ? await repository.FindJobAsync(parentId, ct) : null;
        var filename = SafeFileName(parent?.FileName ?? job.FileName);
        // 逐页任务或已拆页的单页文件写页码，其余情况写“整份文档”。
        var display = job.ParentId is not null || job.PagesPrepared && job.PageCount == 1
            ? $"{filename} - 第{job.PageNumber}页" : $"{filename} - 整份文档";
        // 子任务没有自己的文档 ID，就用父任务的，同一次上传的日志才能归到一起。
        return new(job.Id, job.ParentId ?? job.Id, filename, job.PageNumber, display);
    }

    // 转成日志作用域用的键值表；作用域里的字段会自动附加到该范围内每条日志上。
    public Dictionary<string, object?> Scope() => new()
    {
        ["ScoreId"] = ScoreId,
        ["DocumentId"] = DocumentId,
        ["FileName"] = FileName,
        ["PageNumber"] = PageNumber,
        ["ScoreName"] = ScoreName
    };

    // 把代码里的操作代号翻成中文，日志直接可读，不用再对照表。
    public static string Operation(string value) => value switch
    {
        "prepare" => "PDF 拆页",
        "recognize" => "识谱",
        "render" => "标注导出",
        "merge" => "整份 PDF 合并",
        _ => value
    };
}
