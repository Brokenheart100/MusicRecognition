using System.Security.Cryptography;
using System.Text;
using MusicRecognition.Application;
using MusicRecognition.Application.Commands;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Handlers;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Application.Serialization;
using MusicRecognition.Domain.Entities;
using MusicRecognition.Domain.Enums;
using MusicRecognition.Domain.Services;
using MusicRecognition.Domain.ValueObjects;
using Xunit;

public sealed class ScoreRevisionTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Manual_note_is_appended_with_a_negative_inter_id_and_new_revision()
    {
        var job = Job("Ready", 1);
        var raw = RawNote(5);
        var repo = new RevisionRepository(job, Snapshot(raw));
        var service = new ScoreService(repo, new Storage());
        var manualId = Guid.NewGuid();

        var view = await service.CorrectAsync(job.Id, Token, new RevisionRequest(1, [], NumberingOptions.FixedC,
            AddedNotes: [new NoteAddition(manualId, 1, 1, 1, 1, 1, new Pitch(Step.E, 0, 4), new Rect(20, 20, 8, 8))]), CancellationToken.None);

        Assert.Equal(2, view.Version);
        Assert.Equal(2, view.Score.Notes.Length);
        var added = Assert.Single(view.Score.Notes, note => note.Id == manualId);
        Assert.True(added.Manual);
        Assert.Equal(-1, added.InterId);
        Assert.Equal(3, added.Label.Degree);
        Assert.Equal(2, repo.Revisions.Count);
        Assert.Equal(1, repo.Revisions[0].Version);
        Assert.Equal("Ready", repo.Revisions[0].State);
        Assert.Equal("Queued", repo.Revisions[1].State);
    }

    [Fact]
    public async Task Manual_note_can_be_removed_but_recognized_note_cannot()
    {
        var job = Job("Ready", 1);
        var raw = RawNote(5);
        var manual = new Note(Guid.NewGuid(), -1, 1, 1, 1, 1, new Pitch(Step.E, 0, 4), new Rect(20, 20, 8, 8),
            Numbering.Convert(new Pitch(Step.E, 0, 4), 1, NumberingOptions.FixedC), System: 1, Manual: true);
        var repo = new RevisionRepository(job, Snapshot(raw, manual));
        var service = new ScoreService(repo, new Storage());

        var view = await service.CorrectAsync(job.Id, Token, new RevisionRequest(1, [], NumberingOptions.FixedC,
            RemovedNoteIds: [manual.Id]), CancellationToken.None);

        Assert.Single(view.Score.Notes);
        Assert.Equal(raw.Id, view.Score.Notes[0].Id);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CorrectAsync(job.Id, Token,
            new RevisionRequest(2, [], NumberingOptions.FixedC, RemovedNoteIds: [raw.Id]), CancellationToken.None));
        Assert.Contains("manually added", error.Message);
    }

    [Fact]
    public async Task Manual_note_rejects_duplicate_id_and_out_of_page_bounds()
    {
        var job = Job("Ready", 1);
        var raw = RawNote(5);
        var repo = new RevisionRepository(job, Snapshot(raw));
        var service = new ScoreService(repo, new Storage());

        var duplicate = new NoteAddition(raw.Id, 1, 1, 1, 1, 1, new Pitch(Step.C, 0, 4), new Rect(20, 20, 8, 8));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CorrectAsync(job.Id, Token,
            new RevisionRequest(1, [], NumberingOptions.FixedC, AddedNotes: [duplicate]), CancellationToken.None));

        var outside = new NoteAddition(Guid.NewGuid(), 1, 1, 1, 1, 1, new Pitch(Step.C, 0, 4), new Rect(995, 20, 8, 8));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CorrectAsync(job.Id, Token,
            new RevisionRequest(1, [], NumberingOptions.FixedC, AddedNotes: [outside]), CancellationToken.None));
    }

    private static Note RawNote(int interId) => new(Guid.NewGuid(), interId, 1, 1, 1, 1,
        new Pitch(Step.C, 0, 4), new Rect(10, 10, 8, 8),
        Numbering.Convert(new Pitch(Step.C, 0, 4), 1, NumberingOptions.FixedC), System: 1);

    private static ScoreSnapshot Snapshot(params Note[] notes) => new(1000, 1000, 10, "Audiveris",
        NumberingOptions.FixedC, notes, []);

    private static ScoreJob Job(string state, int revision) => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token))),
        State = state,
        CurrentRevision = revision,
    };

    private sealed class Storage : IScoreStorage
    {
        public string JobDirectory(Guid id) => throw new NotSupportedException();
        public Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct) => throw new NotSupportedException();
        public void DiscardSource(Guid id, string extension) { }
    }

    private sealed class RevisionRepository(ScoreJob job, ScoreSnapshot snapshot) : IScoreRepository
    {
        public List<ScoreJob> Jobs { get; } = [job];
        public List<ScoreRevision> Revisions { get; } = [new() { JobId = job.Id, Version = 1, State = "Ready", Snapshot = ScoreJson.Write(snapshot) }];

        public Task<ScoreJob?> FindJobAsync(Guid id, CancellationToken ct) => Task.FromResult(Jobs.SingleOrDefault(j => j.Id == id));
        public Task<ScoreJob?> FindRequestAsync(string hash, CancellationToken ct) => Task.FromResult<ScoreJob?>(null);
        public Task<ScoreRevision?> FindRevisionAsync(Guid id, int version, CancellationToken ct) => Task.FromResult(Revisions.SingleOrDefault(r => r.JobId == id && r.Version == version));
        public Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct) => Task.FromResult(Jobs.Where(j => j.ParentId == parentId).ToArray());
        public Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct) => Task.FromResult(Array.Empty<PageJobView>());
        public void Add(ScoreJob value) => Jobs.Add(value);
        public void Add(ScoreRevision revision) => Revisions.Add(revision);
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
        public void Clear() { }
    }
}
