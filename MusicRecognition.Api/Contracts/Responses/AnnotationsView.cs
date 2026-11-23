using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Api.Contracts.Responses;

// 给浏览器画简谱用的“排版清单”（layout）：一页乐谱多大、字形长什么样、数字贴哪儿。
// 包含页面几何（PdfGeometryView）、字形表（GlyphsView）和每个数字的标注信息。
// 前端不重新算乐理，只按这份清单画到原谱上，所以字段就是前后端契约，改名会弄坏界面。
// The browser layout projection. Full diagnostic artifacts remain available as files.
public sealed record AnnotationsView(double Width, double Height, string Color, PdfGeometryView? PdfGeometry,
    GlyphsView Glyphs, AnnotationView[] Annotations);
// 原谱转图片的几何参数：CropBox 页面区域、Rotation 旋转角、RasterDpi 清晰度。
public sealed record PdfGeometryView(double[] CropBox, int Rotation, double RasterDpi);
// 字形表：把简谱数字存成矢量轮廓下发，前端不装乐谱字体也能画。
public sealed record GlyphsView(double UnitsPerEm, Dictionary<string, GlyphView> Characters);
public sealed record GlyphView(string Path, double Advance);
// 一个简谱数字标注的“贴纸”：贴在哪颗音符、写什么、多大字号、贴哪儿；Dots 是高低八度点。
public sealed record AnnotationView(Guid NoteId, int InterId, string Status, string Text, string Degree,
    int Alteration, int OctaveDots, double FontSize, double Width, double Height, double[] BaselineOffset,
    double[][] Dots, double[] Bounds, double[]? LabelBounds);
