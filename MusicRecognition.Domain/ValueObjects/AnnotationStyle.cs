namespace MusicRecognition.Domain.ValueObjects;

// 标注样式（annotation style）：整份谱上简谱数字用多大字号、什么颜色。
// 字号单位是 PDF 点（point，1/72 英寸）；null 表示交给渲染程序按谱面留白自动选。
// 颜色用六位十六进制写法，默认纯黑 #000000——用户明确要求黑色标注。
public sealed record AnnotationStyle(double? FontSizePoints = null, string Color = "#000000")
{
    public static AnnotationStyle Default => new();
    // 校验字号 3~16 pt、颜色是 #RRGGBB；超出范围直接报错，避免生成看不清或越界的字。
    public void Validate()
    {
        if (FontSizePoints is { } size && (!double.IsFinite(size) || size is < 3 or > 16))
            throw new ArgumentException("Annotation font size must be between 3 and 16 PDF points, or null for automatic sizing.");
        if (Color is null || !System.Text.RegularExpressions.Regex.IsMatch(Color, "\\A#[0-9a-fA-F]{6}\\z"))
            throw new ArgumentException("Annotation color must be a six-digit hex color, such as #000000.");
    }
}
