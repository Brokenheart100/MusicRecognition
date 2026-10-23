using MusicRecognition.Domain;
using Xunit;

public class PlacementTests
{
    [Fact]
    public void Automatic_and_hidden_placements_do_not_require_coordinates()
    {
        new AnnotationPlacement(false,null,null).Validate(100,200);
        new AnnotationPlacement(true,null,null).Validate(100,200);
        new AnnotationPlacement(false,25.5,100.25).Validate(100,200);
    }
    [Theory]
    [InlineData(-1,10)] [InlineData(100,10)] [InlineData(10,200)]
    [InlineData(double.NaN,10)] [InlineData(10,double.PositiveInfinity)]
    public void Invalid_positions_are_rejected(double x,double y) =>
        Assert.Throws<ArgumentException>(()=>new AnnotationPlacement(false,x,y).Validate(100,200));
    [Fact]
    public void A_single_axis_cannot_be_saved() =>
        Assert.Throws<ArgumentException>(()=>new AnnotationPlacement(false,10,null).Validate(100,200));
}
