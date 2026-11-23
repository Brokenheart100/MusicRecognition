using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.Commands;

// 用户在校对页点"保存修改"时提交的整包内容（revision request），由 API 接收后交给应用层。
// 包含：期望的修订版本号、音高纠正、换算设置、标注位置/样式调整，以及手工补录和删除的音符。
// ExpectedRevision 是并发保护：版本对不上说明别人先改过，本次保存会被拒绝，避免覆盖。
// 下面三个小记录是三类单音修改；样式传 null 表示恢复整份谱的默认样式。
public sealed record Correction(Guid NoteId, Pitch Pitch);
public sealed record PlacementChange(Guid NoteId, AnnotationPlacement Placement);
public sealed record StyleChange(Guid NoteId, AnnotationStyle? Style);
// 手工补录的音符：客户端生成 Id，服务端分配负数 InterId 并标记 Manual，渲染时和识别结果一起排版。
public sealed record NoteAddition(Guid Id, int Measure, int Part, int Staff, int Voice, int System,
    Pitch Pitch, Rect Bounds, AnnotationPlacement? Placement = null, AnnotationStyle? Style = null);
public sealed record RevisionRequest(int ExpectedRevision, Correction[] Corrections, NumberingOptions Numbering,
    PlacementChange[]? Placements = null, AnnotationStyle? AnnotationStyle = null, StyleChange[]? StyleChanges = null,
    NoteAddition[]? AddedNotes = null, Guid[]? RemovedNoteIds = null);
