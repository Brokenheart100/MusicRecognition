using MusicRecognition.Domain.Enums;

namespace MusicRecognition.Domain.ValueObjects;

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
