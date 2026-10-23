namespace MusicRecognition.Application;

// The browser layout projection. Full diagnostic artifacts remain available as files.
public sealed record AnnotationsView(double Width, double Height, string Color, PdfGeometryView? PdfGeometry,
    GlyphsView Glyphs, AnnotationView[] Annotations);
public sealed record PdfGeometryView(double[] CropBox, int Rotation, double RasterDpi);
public sealed record GlyphsView(double UnitsPerEm, Dictionary<string, GlyphView> Characters);
public sealed record GlyphView(string Path, double Advance);
public sealed record AnnotationView(Guid NoteId, int InterId, string Status, string Text, string Degree,
    int Alteration, int OctaveDots, double FontSize, double Width, double Height, double[] BaselineOffset,
    double[][] Dots, double[] Bounds, double[]? LabelBounds);
