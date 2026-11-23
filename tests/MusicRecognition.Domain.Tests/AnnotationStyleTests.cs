using MusicRecognition.Domain.ValueObjects;
using Xunit;

public class AnnotationStyleTests
{
    [Fact]
    public void Default_uses_automatic_sizing_and_black()
    {
        AnnotationStyle.Default.Validate();
        Assert.Null(AnnotationStyle.Default.FontSizePoints);
        Assert.Equal("#000000", AnnotationStyle.Default.Color);
        new AnnotationStyle(null).Validate(); // Explicit automatic sizing remains supported.
    }

    [Theory]
    [InlineData(2.9)] [InlineData(16.1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void Invalid_font_sizes_are_rejected(double size) =>
        Assert.Throws<ArgumentException>(() => new AnnotationStyle(size).Validate());

    [Theory]
    [InlineData("red")] [InlineData("#000")] [InlineData("#00000000")] [InlineData("#000000\n")] [InlineData(null)]
    public void Only_opaque_hex_colors_are_accepted(string? color) =>
        Assert.Throws<ArgumentException>(() => new AnnotationStyle(6, color!).Validate());

    [Theory]
    [InlineData(3, "#000000")] [InlineData(16, "#abcdef")]
    public void Boundary_sizes_and_hex_colors_are_supported(double size, string color) =>
        new AnnotationStyle(size, color).Validate();
}
