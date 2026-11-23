namespace MusicRecognition.Domain.ValueObjects;

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
