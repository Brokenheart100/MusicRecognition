namespace MusicRecognition.Application;

public sealed record ReflowNote(string XmlId, Guid NoteId, int Degree, int Alteration, int OctaveDots,
    double? FontSizePoints, string Color, bool Hidden);
public sealed record ReflowView(int FormatVersion, string MusicXml, ReflowNote[] Notes, int SourceHeadCount,
    int MappedHeadCount, int OmittedHeadCount, string[] Warnings, string ValidationPolicy);
public interface IReflowEngine
{
    Task<ReflowView> PrepareAsync(Guid id, int version, MusicRecognition.Domain.ScoreSnapshot snapshot, CancellationToken ct);
}

public sealed class ReflowService(IScoreService scores, IReflowEngine engine)
{
    public async Task<ReflowView> GetAsync(Guid id, int? version, string token, CancellationToken ct)
    {
        var job = await scores.GetAsync(id, token, ct);
        if (job.PageCount > 1) throw new RequestFailure(409, "page-required", "Select an individual page for reflow.");
        var revision = await scores.RevisionAsync(id, version, token, ct);
        if (revision.State != "Ready") throw new RequestFailure(409, "export-not-ready", "Save and finish exporting the revision before reflow.");
        return await engine.PrepareAsync(id, revision.Version, revision.Score, ct);
    }
}
