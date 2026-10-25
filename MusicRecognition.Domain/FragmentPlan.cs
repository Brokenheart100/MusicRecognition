namespace MusicRecognition.Domain;

public sealed record FragmentCrop(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (new[] { X,Y,Width,Height }.Any(v => !double.IsFinite(v)) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X+Width > 1.000001 || Y+Height > 1.000001)
            throw new ArgumentException("裁剪区域必须位于原图内，坐标使用 0–1 的比例。");
    }
}
public sealed record FragmentSelection(int SourceIndex, FragmentCrop? Crop = null, int StartMeasure = 1, int? EndMeasure = null);
public sealed record FragmentPlan(string Title, FragmentSelection[] Fragments, bool ReviewConfirmed = false)
{
    public void Validate(int sourceCount)
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 120 || Title.Any(char.IsControl)) throw new ArgumentException("标题需要 1–120 个字符。");
        if (Fragments is null || Fragments.Length is < 1 or > 12 || Fragments.Any(f => f is null) || Fragments.Select(f => f.SourceIndex).Distinct().Count() != Fragments.Length)
            throw new ArgumentException("请选择 1–12 个不重复的图片片段。");
        foreach (var f in Fragments)
        {
            if (f.SourceIndex < 0 || f.SourceIndex >= sourceCount || f.StartMeasure is < 1 or > 5000 || f.EndMeasure is { } end && (end < f.StartMeasure || end > 5000))
                throw new ArgumentException("片段索引或保留小节范围无效。");
            f.Crop?.Validate();
        }
    }
}
