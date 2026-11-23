using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Domain.Entities;

// 一个音符（note）在程序里的完整档案：是什么音、在哪一小节、属于哪个五线谱表和声部，
// 识别图上的符头画在哪个矩形里，以及它对应的简谱数字和标注位置。
// 它是识别引擎输出的最小单位，用户逐音校对时改的也是它。
// InterId 是识别引擎（Audiveris）的内部编号，只在和引擎自己的产物对照时使用。
// 位置和样式可以为 null：表示还没人工调整，渲染时由程序自动选位置、用默认样式。
// System 是原始谱表组编号，自动排版要靠它找到符头所在的五线谱；旧快照没有这个字段时为 null。
// Manual 标记用户手工补录的音符：它没有引擎内部编号，也不属于原始 MusicXML，删除时只允许删这一类。
// RegionRecognized 标记区域重识别得到的音符：它来自一次临时识别，几何和和弦信息由 labels 传给渲染脚本。
// ChordId 是引擎的和弦编号，自动排版把同一和弦的数字竖排；StaffInPart 用于和 MusicXML 对账。
public sealed record Note(Guid Id, int InterId, int Measure, int Part, int Staff, int Voice,
    Pitch Pitch, Rect Bounds, NumberedLabel Label, AnnotationPlacement? Placement = null, AnnotationStyle? Style = null,
    int? System = null, bool Manual = false, bool RegionRecognized = false, int? ChordId = null, int StaffInPart = 0);
