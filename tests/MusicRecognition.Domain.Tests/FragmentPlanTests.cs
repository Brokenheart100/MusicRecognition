using MusicRecognition.Domain;
using Xunit;

public class FragmentPlanTests
{
    [Fact]
    public void Ordered_selection_can_skip_overlapping_complete_measures() =>
        new FragmentPlan("钢琴片段",[new(1,null,2,4),new(0)]).Validate(2);

    [Fact]
    public void Duplicate_or_unknown_sources_are_rejected()
    {
        Assert.Throws<ArgumentException>(()=>new FragmentPlan("x",[new(0),new(0)]).Validate(2));
        Assert.Throws<ArgumentException>(()=>new FragmentPlan("x",[new(2)]).Validate(2));
    }
    [Fact]
    public void Reversed_ranges_and_outside_crops_are_rejected()
    {
        Assert.Throws<ArgumentException>(()=>new FragmentPlan("x",[new(0,null,4,2)]).Validate(1));
        Assert.Throws<ArgumentException>(()=>new FragmentCrop(.5,0,.6,1).Validate());
        Assert.Throws<ArgumentException>(()=>new FragmentCrop(double.NaN,0,1,1).Validate());
    }
}
