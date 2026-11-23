namespace MusicRecognition.Domain.ValueObjects;

// 矩形（rect）：用像素描述识别图上的一块方形区域，X/Y 是左上角坐标，Width/Height 是宽高。
// 目前主要用来记录符头占的范围：自动布局标注时要避开它，不能把数字盖在符头上。
// 坐标原点在识别图左上角、向右向下为正；和 PDF 坐标不同，换算由 PdfGeometry 负责。
public sealed record Rect(double X, double Y, double Width, double Height);
