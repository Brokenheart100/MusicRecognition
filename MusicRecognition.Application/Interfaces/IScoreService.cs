using MusicRecognition.Application.Commands;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;

namespace MusicRecognition.Application.Interfaces;

// 乐谱用例的接口（application service）：API 层只调用这里，不关心数据存哪、识别怎么跑。
// 一组方法覆盖完整流程：上传导入、查任务、读修订、保存校对、取消/重试、下载产物、整份导出。
// 所有方法都要访问令牌（token），权限校验统一在实现里通过 ScoreAccess 完成。
public interface IScoreService
{
    // 上传文件：保存原件并创建任务；idempotencyKey 让重复提交不会生成两个任务。
    Task<JobView> ImportAsync(Stream stream, string filename, string token, string idempotencyKey, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris);
    Task<JobView> GetAsync(Guid id, string token, CancellationToken ct);
    // 读取指定版本的完整谱面快照，供前端渲染校对页。
    Task<RevisionView> RevisionAsync(Guid id, int version, string token, CancellationToken ct);
    // 保存校对：音高、换算设置、标注位置和样式合成一个新修订。
    Task<RevisionView> CorrectAsync(Guid id, string token, RevisionRequest request, CancellationToken ct);
    Task<JobView> CancelAsync(Guid id, string token, CancellationToken ct);
    Task<JobView> RetryAsync(Guid id, string token, CancellationToken ct, RetryRequest? request = null);
    // 按名字取下载文件：source、score.mxl、annotated.pdf、annotations.json、metrics.json。
    Task<StoredFile> ArtifactAsync(Guid id, int? version, string name, string token, CancellationToken ct);
    // 整份导出：创建合并任务并返回状态；之后可用版本号查询进度。
    Task<DocumentExportView> ExportAsync(Guid id, string token, CancellationToken ct);
    Task<DocumentExportView> ExportStatusAsync(Guid id, int version, string token, CancellationToken ct);
}
