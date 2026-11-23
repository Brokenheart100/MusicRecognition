namespace MusicRecognition.Domain.ValueObjects;

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
