namespace MusicRecognition.Domain.ValueObjects;

public sealed record AnnotationPlacement(bool Hidden, double? X, double? Y)
{
    public void Validate(int pageWidth, int pageHeight)
    {
        if (X.HasValue != Y.HasValue || (X.HasValue &&
            (!double.IsFinite(X.Value) || !double.IsFinite(Y!.Value) || X < 0 || Y < 0 || X >= pageWidth || Y >= pageHeight)))
            throw new ArgumentException("Placement requires both finite coordinates inside the recognition page, or neither for automatic layout.");
    }
}
