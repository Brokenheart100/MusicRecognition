using MusicRecognition.Domain.Entities;
using MusicRecognition.Domain.Enums;
using MusicRecognition.Domain.Services;
using MusicRecognition.Domain.ValueObjects;
using Xunit;

public sealed class RegionMergeTests
{
    [Fact]
    public void Merge_replaces_notes_inside_the_region_and_keeps_the_rest()
    {
        var outside = Raw(1, 10, 10, chordId: 7);
        var insideRaw = Raw(2, 110, 10, chordId: 8);
        var insideManual = Manual(-1, 120, 20);
        var previous = Snapshot(outside, insideRaw, insideManual);
        var recognized = Snapshot(Raw(20, 112, 12, chordId: 99), Raw(21, 112, 30, chordId: 99), Raw(22, 500, 500, chordId: 100));

        var merged = RegionMerge.Merge(previous, recognized, new Rect(100, 0, 100, 100));

        Assert.Equal(3, merged.Notes.Length);
        Assert.Contains(merged.Notes, note => note.Id == outside.Id);
        Assert.DoesNotContain(merged.Notes, note => note.Id == insideRaw.Id || note.Id == insideManual.Id);
        var added = merged.Notes.Where(note => note.RegionRecognized).ToArray();
        Assert.Equal(2, added.Length);
        Assert.All(added, note => Assert.True(note.InterId < 0));
        Assert.Equal([-3, -2], added.Select(note => note.InterId).OrderBy(value => value).ToArray());
        Assert.All(added, note => Assert.Equal(Numbering.Convert(note.Pitch, note.Measure, merged.Numbering), note.Label));
        Assert.All(added, note => Assert.Null(note.Placement));
        Assert.All(added, note => Assert.Null(note.Style));
        // 同一新和弦映射到同一个负数 ChordId，区域外旧和弦编号不参与。
        Assert.Equal(added[0].ChordId, added[1].ChordId);
        Assert.True(added[0].ChordId < 0);
    }

    [Fact]
    public void Merge_allocates_new_chord_ids_below_existing_region_chords()
    {
        var previous = Snapshot(Raw(1, 10, 10, chordId: 7), Raw(-1, 500, 500, chordId: -2) with { RegionRecognized = true });
        var recognized = Snapshot(Raw(20, 50, 50, chordId: 99));

        var merged = RegionMerge.Merge(previous, recognized, new Rect(0, 0, 100, 100));

        var added = merged.Notes.Single(note => note.RegionRecognized && note.InterId < -1);
        Assert.Equal(-3, added.ChordId);
    }

    [Fact]
    public void Merge_ignores_recognized_notes_outside_the_region()
    {
        var previous = Snapshot(Raw(1, 500, 500));
        var recognized = Snapshot(Raw(2, 50, 50), Raw(3, 600, 600));

        var merged = RegionMerge.Merge(previous, recognized, new Rect(0, 0, 100, 100));

        Assert.Equal(2, merged.Notes.Length);
        Assert.Equal(1, merged.Notes[0].InterId);
        Assert.DoesNotContain(merged.Notes, note => note.InterId == 3);
    }

    [Fact]
    public void Empty_region_result_is_rejected()
    {
        var previous = Snapshot(Raw(1, 110, 10));
        var recognized = Snapshot(Raw(2, 500, 500));

        var error = Assert.Throws<InvalidOperationException>(() => RegionMerge.Merge(previous, recognized, new Rect(100, 0, 100, 100)));

        Assert.Contains("no notes", error.Message);
    }

    [Fact]
    public void Region_must_be_a_positive_rectangle_inside_the_page()
    {
        var snapshot = Snapshot(Raw(1, 10, 10));

        Assert.Throws<ArgumentException>(() => RegionMerge.Merge(snapshot, snapshot, new Rect(-1, 0, 10, 10)));
        Assert.Throws<ArgumentException>(() => RegionMerge.Merge(snapshot, snapshot, new Rect(0, 0, 0, 10)));
        Assert.Throws<ArgumentException>(() => RegionMerge.Merge(snapshot, snapshot, new Rect(995, 0, 20, 10)));
    }

    private static Note Raw(int interId, double x, double y, int? chordId = null) => new(Guid.NewGuid(), interId, 1, 1, 1, 1,
        new Pitch(Step.C, 0, 4), new Rect(x, y, 8, 8),
        Numbering.Convert(new Pitch(Step.C, 0, 4), 1, NumberingOptions.FixedC), System: 1, ChordId: chordId);

    private static Note Manual(int interId, double x, double y) => new(Guid.NewGuid(), interId, 1, 1, 1, 1,
        new Pitch(Step.E, 0, 4), new Rect(x, y, 8, 8),
        Numbering.Convert(new Pitch(Step.E, 0, 4), 1, NumberingOptions.FixedC), System: 1, Manual: true);

    private static ScoreSnapshot Snapshot(params Note[] notes) => new(1000, 1000, 10, "Audiveris",
        NumberingOptions.FixedC, notes, []);
}
