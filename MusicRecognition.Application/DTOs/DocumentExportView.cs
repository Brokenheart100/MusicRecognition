namespace MusicRecognition.Application.DTOs;

// 指向某一页：页码（从 1 开始）、这页的任务 ID、本次合并要采用的修订版本号。
public sealed record DocumentPageRef(int Number, Guid Id, int Version);
// 写入任务目录、供合并脚本读取的清单；Kind 固定为 document，脚本据此确认清单类型。
public sealed record DocumentExportSnapshot(DocumentPageRef[] Pages, string Kind = "document");
// 接口返回给前端的导出状态：版本号、状态、页列表和错误码。
public sealed record DocumentExportView(int Version, string State, DocumentPageRef[] Pages, string? ErrorCode);
