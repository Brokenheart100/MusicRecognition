using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.Interfaces;

// 识别引擎的接口（port）：应用层只认识这组能力，具体用 Audiveris 还是 Homr 由基础设施层决定。
// 所有方法都是异步的：背后要启动外部进程读谱或排版，耗时从几秒到几分钟。
// 产物都写在任务自己的目录里，应用层拿到的只是谱面快照、页数这类小结果。
public interface IRecognitionEngine
{
    // 识别整页：输出谱面快照；preprocessing 和 engine 决定预处理方式和用哪个引擎。
    Task<ScoreSnapshot> RecognizeAsync(Guid id, string extension, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris, string? rawDirectory = null);
    // 按修订渲染标注产物（PDF/JSON）；version 决定产物目录，同一修订只渲染一次。
    Task RenderAsync(Guid id, int version, string extension, ScoreSnapshot snapshot, CancellationToken ct);
    // 拆页并数页数：多页 PDF 每页拆成一个子任务，后续逐页识别（页数上限在脚本里）。
    Task<PreparedDocument> PrepareDocumentAsync(Guid id, string extension, CancellationToken ct);
    // 按清单把各页已导出的标注 PDF 合并成整份 PDF。
    Task MergeDocumentAsync(Guid id, int version, DocumentExportSnapshot snapshot, CancellationToken ct);
}
