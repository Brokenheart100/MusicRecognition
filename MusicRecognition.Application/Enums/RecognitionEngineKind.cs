namespace MusicRecognition.Application.Enums;

// 识别引擎（recognition engine）：能把五线谱图片读成音符的程序有多个实现，这里列出可选的两个。
// Audiveris 是默认引擎，能给出符头坐标和 MusicXML，结果较稳定；Homr 是备选引擎。
// 选择会影响识别质量和产物：Homr 给出的坐标是近似锚点，通常还需要用户人工确认标注位置。
public enum RecognitionEngineKind { Audiveris, Homr }
