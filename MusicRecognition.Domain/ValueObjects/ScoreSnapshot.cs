using MusicRecognition.Domain.Entities;

namespace MusicRecognition.Domain.ValueObjects;

// 谱面快照（score snapshot）：一次识别完成后的完整结果，也是修订 JSON 的根对象。
// Width/Height 是识别图像素尺寸，Interline 是五线谱相邻两线的像素距离；
// 渲染时靠这三个数字推算字号和可用的空白位置；Engine 记录用的是哪个识别引擎。
// Notes 是全部音符；Warnings 是引擎给出、需要用户留意的问题，例如音高一致性告警。
// Numbering 是当前换算设置，AnnotationStyle 是整份谱的标注样式，为空表示用默认黑色。
public sealed record ScoreSnapshot(int Width, int Height, int Interline, string Engine,
    NumberingOptions Numbering, Note[] Notes, string[] Warnings, AnnotationStyle? AnnotationStyle = null);
