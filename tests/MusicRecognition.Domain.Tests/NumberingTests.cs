using MusicRecognition.Domain;
using Xunit;

public class NumberingTests
{
    [Theory]
    [InlineData(Step.C,0,4,1,0,0)]
    [InlineData(Step.B,0,3,7,0,-1)]
    [InlineData(Step.C,0,5,1,0,1)]
    [InlineData(Step.D,1,4,2,1,0)]
    [InlineData(Step.E,-1,4,3,-1,0)]
    [InlineData(Step.F,2,2,4,2,-2)]
    public void FixedC_preserves_spelling_and_octaves(Step step,int alter,int octave,int degree,int accidental,int dots)
    {Assert.Equal(new NumberedLabel(degree,accidental,dots),Numbering.Convert(new(step,alter,octave),1,NumberingOptions.FixedC));}
    [Fact]
    public void Enharmonic_pitches_are_not_collapsed()
    {
        var ds=new Pitch(Step.D,1,4);var eb=new Pitch(Step.E,-1,4);
        Assert.Equal(ds.Midi,eb.Midi);
        Assert.NotEqual(Numbering.Convert(ds,1,NumberingOptions.FixedC),Numbering.Convert(eb,1,NumberingOptions.FixedC));
    }
    [Fact]
    public void Major_key_reference_and_key_change_apply_at_boundary()
    {
        var options=new NumberingOptions(NumberingMode.MovableDo,[new(1,new(Step.G,0,4),ScaleKind.Major),new(5,new(Step.D,0,4),ScaleKind.Major)]);
        Assert.Equal(new(7,0,-1),Numbering.Convert(new(Step.F,1,4),4,options));
        Assert.Equal(new(3,0,0),Numbering.Convert(new(Step.F,1,4),5,options));
        Assert.Equal(new(1,0,0),Numbering.Convert(new(Step.G,0,4),1,options));
    }
    [Fact]
    public void Natural_minor_uses_la_based_six()
    {
        var options=new NumberingOptions(NumberingMode.MovableDo,[new(1,new(Step.A,0,4),ScaleKind.NaturalMinor)]);
        Assert.Equal(new(6,0,0),Numbering.Convert(new(Step.A,0,4),1,options));
        Assert.Equal(new(1,0,0),Numbering.Convert(new(Step.C,0,4),1,options));
        Assert.Equal(new(5,1,0),Numbering.Convert(new(Step.G,1,4),1,options));
    }
    [Fact]
    public void Missing_initial_key_and_duplicate_changes_are_rejected()
    {
        Assert.Throws<ArgumentException>(()=>new NumberingOptions(NumberingMode.MovableDo,[]).Validate());
        Assert.Throws<ArgumentException>(()=>new NumberingOptions(NumberingMode.MovableDo,[new(1,new(Step.C,0,4),ScaleKind.Major),new(1,new(Step.G,0,4),ScaleKind.Major)]).Validate());
    }
    [Theory]
    [InlineData(-3,4)] [InlineData(3,4)] [InlineData(0,-1)] [InlineData(0,10)]
    public void Invalid_pitch_is_rejected(int alter,int octave) => Assert.Throws<ArgumentException>(()=>new Pitch(Step.C,alter,octave));
    [Theory]
    [InlineData(0,12,18)] [InlineData(90,581.55,12)] [InlineData(180,287.25,581.55)] [InlineData(270,18,287.25)]
    public void Fractional_crop_rotation_uses_physical_extent(int rotation,double dx,double dy)
    {
        var geometry=new PdfGeometry(10.25,20.5,110,220.35,rotation);
        var pdf=geometry.ImageToPdf(12,18);
        Assert.Equal(14.25,pdf.X,8); Assert.Equal(214.35,pdf.Y,8);
        var display=geometry.ImageToDisplay(12,18);
        Assert.Equal(dx,display.X,8);Assert.Equal(dy,display.Y,8);
    }
}
