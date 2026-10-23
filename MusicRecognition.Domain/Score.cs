namespace MusicRecognition.Domain;

public enum Step { C, D, E, F, G, A, B }
public enum NumberingMode { FixedC, MovableDo }
public enum ScaleKind { Major, NaturalMinor }

public sealed record Pitch
{
    public Step Step { get; init; }
    public int Alter { get; init; }
    public int Octave { get; init; }
    public Pitch(Step step, int alter, int octave)
    {
        if (!Enum.IsDefined(step) || alter is < -2 or > 2 || octave is < 0 or > 9)
            throw new ArgumentException("Pitch requires C..B, alteration -2..2, octave 0..9.");
        Step = step; Alter = alter; Octave = octave;
    }
    public int Midi => (Octave + 1) * 12 + new[] { 0, 2, 4, 5, 7, 9, 11 }[(int)Step] + Alter;
    public int Diatonic => Octave * 7 + (int)Step;
}

public sealed record KeyChange(int StartMeasure, Pitch Tonic, ScaleKind Scale);
public sealed record NumberingOptions(NumberingMode Mode, KeyChange[] Keys)
{
    public static NumberingOptions FixedC => new(NumberingMode.FixedC, []);
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Keys is null || Keys.Length > 100)
            throw new ArgumentException("Invalid numbering options.");
        if (Keys.Any(k => k is null || k.Tonic is null || k.StartMeasure < 1 || !Enum.IsDefined(k.Scale)) ||
            Keys.Select(k => k.StartMeasure).Distinct().Count() != Keys.Length)
            throw new ArgumentException("Key changes require unique positive measure numbers and explicit tonic.");
        if (Mode == NumberingMode.MovableDo && !Keys.Any(k => k.StartMeasure == 1))
            throw new ArgumentException("Movable do requires an explicit key starting at measure 1.");
    }
}

public sealed record NumberedLabel(int Degree, int Alteration, int OctaveDots);
public static class Numbering
{
    public static NumberedLabel Convert(Pitch pitch, int measure, NumberingOptions options)
    {
        options.Validate();
        if (measure < 1) throw new ArgumentException("Measure starts at 1.");
        var doIndex = 28; var doMidi = 60;
        if (options.Mode == NumberingMode.MovableDo)
        {
            var key = options.Keys.Where(k => k.StartMeasure <= measure).MaxBy(k => k.StartMeasure)!;
            doIndex = key.Tonic.Diatonic; doMidi = key.Tonic.Midi;
            if (key.Scale == ScaleKind.NaturalMinor) { doIndex -= 5; doMidi -= 9; }
        }
        var relative = pitch.Diatonic - doIndex;
        var octave = (int)Math.Floor(relative / 7d);
        var degree = relative - octave * 7;
        var expected = doMidi + octave * 12 + new[] { 0, 2, 4, 5, 7, 9, 11 }[degree];
        return new(degree + 1, pitch.Midi - expected, octave);
    }
}

public sealed record Rect(double X, double Y, double Width, double Height);
public sealed record AnnotationPlacement(bool Hidden, double? X, double? Y)
{
    public void Validate(int pageWidth, int pageHeight)
    {
        if (X.HasValue != Y.HasValue || (X.HasValue &&
            (!double.IsFinite(X.Value) || !double.IsFinite(Y!.Value) || X < 0 || Y < 0 || X >= pageWidth || Y >= pageHeight)))
            throw new ArgumentException("Placement requires both finite coordinates inside the recognition page, or neither for automatic layout.");
    }
}
public sealed record Note(Guid Id, int InterId, int Measure, int Part, int Staff, int Voice,
    Pitch Pitch, Rect Bounds, NumberedLabel Label, AnnotationPlacement? Placement = null, AnnotationStyle? Style = null);
public sealed record AnnotationStyle(double? FontSizePoints = null, string Color = "#000000")
{
    public static AnnotationStyle Default => new();
    public void Validate()
    {
        if (FontSizePoints is { } size && (!double.IsFinite(size) || size is < 3 or > 16))
            throw new ArgumentException("Annotation font size must be between 3 and 16 PDF points, or null for automatic sizing.");
        if (Color is null || !System.Text.RegularExpressions.Regex.IsMatch(Color, "\\A#[0-9a-fA-F]{6}\\z"))
            throw new ArgumentException("Annotation color must be a six-digit hex color, such as #000000.");
    }
}
public sealed record ScoreSnapshot(int Width, int Height, int Interline, string Engine,
    NumberingOptions Numbering, Note[] Notes, string[] Warnings, AnnotationStyle? AnnotationStyle = null);

// Coordinates are always the upright CropBox raster; /Rotate is display metadata.
public sealed record PdfGeometry(double Left, double Bottom, double Right, double Top, int Rotation, double Dpi = 216)
{
    public (double X, double Y) ImageToPdf(double x, double y)
    {
        Validate(); return (Left + x * 72 / Dpi, Top - y * 72 / Dpi);
    }
    public (double X, double Y) ImageToDisplay(double x, double y)
    {
        Validate(); var w = (Right - Left) * Dpi / 72; var h = (Top - Bottom) * Dpi / 72;
        return Rotation switch { 0 => (x, y), 90 => (h - y, x), 180 => (w - x, h - y), _ => (y, w - x) };
    }
    private void Validate()
    {
        if (!double.IsFinite(Left + Bottom + Right + Top + Dpi) || Left >= Right || Bottom >= Top || Dpi <= 0 || Rotation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("Unsupported PDF geometry.");
    }
}
