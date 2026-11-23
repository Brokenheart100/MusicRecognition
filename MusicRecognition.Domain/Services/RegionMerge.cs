using MusicRecognition.Domain.Entities;
using MusicRecognition.Domain.Services;
using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Domain.Services;

// 区域重识别（region re-recognition）的合并规则：用一次临时整页识别结果替换旧快照中
// 落在选区内的音符，选区外的音符、人工修改和位置全部保留。
// 区域内的新音符使用负数 InterId 和 RegionRecognized 标记，渲染时和手工音符一样由 labels 合成，
// 这样 canonical raw（不可变识别产物）不需要被覆盖，旧修订也仍然能正常渲染。
public static class RegionMerge
{
    // 把 recognized 中落在 region 内的音符合并进 previous；返回新的快照，不修改入参。
    public static ScoreSnapshot Merge(ScoreSnapshot previous, ScoreSnapshot recognized, Rect region)
    {
        ValidateRegion(previous, region);
        // 区域内的旧音符（包括手工补录）整块移除；区域外保持原样。
        var kept = previous.Notes.Where(note => !Inside(note.Bounds, region)).ToArray();
        // 新识别结果只取中心落在区域内的音符，区域外的识别结果不参与本次修订。
        var incoming = recognized.Notes.Where(note => Inside(note.Bounds, region)).ToArray();
        if (incoming.Length == 0)
            throw new InvalidOperationException("Region recognition produced no notes inside the selected area.");
        // 负 InterId 继续从现有最小值往下分配，保证不会和 canonical raw 的正数编号冲突。
        var nextInterId = previous.Notes.Length == 0 ? 0 : Math.Min(0, previous.Notes.Min(note => note.InterId));
        var usedIds = kept.Select(note => note.Id).ToHashSet();
        // 新识别结果里的和弦编号只在新识别内部唯一；映射成负数，避免和区域外旧音符的编号撞组。
        var chordIds = new Dictionary<(int? System, int Staff, int ChordId), int>();
        // 上一次区域重识别已经写入的负数和弦编号必须避开，否则不同位置的音符会被当成同一个和弦。
        var previousChordIds = previous.Notes.Where(note => note.ChordId is < 0).Select(note => note.ChordId!.Value).ToArray();
        if (previousChordIds.Length > 0 && previousChordIds.Min() == int.MinValue)
            throw new InvalidOperationException("No internal chord IDs remain.");
        var nextChordId = previousChordIds.Length == 0 ? -1 : previousChordIds.Min() - 1;
        var added = new List<Note>(incoming.Length);
        // 按阅读顺序追加，方便调试时对照原图。
        foreach (var note in incoming.OrderBy(note => note.Bounds.Y).ThenBy(note => note.Bounds.X))
        {
            if (nextInterId == int.MinValue) throw new InvalidOperationException("No internal note IDs remain.");
            nextInterId--;
            int? chordId = null;
            if (note.ChordId is { } sourceChordId)
            {
                var key = (note.System, note.Staff, sourceChordId);
                if (!chordIds.TryGetValue(key, out var mapped))
                {
                    if (nextChordId == int.MinValue) throw new InvalidOperationException("No internal chord IDs remain.");
                    mapped = nextChordId--;
                    chordIds[key] = mapped;
                }
                chordId = mapped;
            }
            added.Add(note with
            {
                // 极端情况下新识别可能复用 Id；保证快照内 Id 唯一，前端选中和删除都靠它。
                Id = usedIds.Add(note.Id) ? note.Id : Guid.NewGuid(),
                InterId = nextInterId,
                Label = Numbering.Convert(note.Pitch, note.Measure, previous.Numbering),
                Placement = null,
                Style = null,
                Manual = false,
                RegionRecognized = true,
                ChordId = chordId,
            });
        }
        return previous with { Notes = [.. kept, .. added] };
    }

    // 区域必须是有限、正尺寸、完全落在页面内的矩形；越界会让渲染坐标错位，直接拒绝。
    public static void ValidateRegion(ScoreSnapshot page, Rect region)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0 ||
            region.X + region.Width > page.Width || region.Y + region.Height > page.Height)
            throw new ArgumentException("Recognition region must be a positive rectangle inside the page.");
    }

    // 用符头矩形的中心点判断归属，避免边缘刚好压线的音符被两边同时处理。
    private static bool Inside(Rect bounds, Rect region)
    {
        var x = bounds.X + bounds.Width / 2;
        var y = bounds.Y + bounds.Height / 2;
        return x >= region.X && x <= region.X + region.Width && y >= region.Y && y <= region.Y + region.Height;
    }
}
