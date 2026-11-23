using System.Security.Cryptography;
using System.Text;
using MusicRecognition.Application;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Handlers;
using MusicRecognition.Application.Interfaces;
using Xunit;

public sealed class ScoreRetryTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Ready_page_retry_queues_without_discarding_the_current_revision()
    {
        var job = Job("Ready", 1);
        var repo = new RetryRepository([job], new ScoreRevision { JobId = job.Id, Version = 1, State = "Ready", Snapshot = "{}" });
        var service = new ScoreService(repo, new Storage());

        var view = await service.RetryAsync(job.Id, Token, CancellationToken.None);

        Assert.Equal("Queued", view.State);
        Assert.Equal(1, view.CurrentRevision);
        Assert.Equal("Queued", job.State);
        Assert.Null(job.BackgroundId);
        Assert.Equal("Ready", repo.Revisions.Single().State);
    }

    [Fact]
    public async Task Retry_is_rejected_while_the_current_layout_is_running()
    {
        var job = Job("Ready", 1);
        var repo = new RetryRepository([job], new ScoreRevision { JobId = job.Id, Version = 1, State = "Processing", Snapshot = "{}" });
        var service = new ScoreService(repo, new Storage());

        var error = await Assert.ThrowsAsync<RequestFailure>(() => service.RetryAsync(job.Id, Token, CancellationToken.None));

        Assert.Equal("layout-in-progress", error.Code);
        Assert.Equal("Ready", job.State);
    }

    [Fact]
    public async Task Retry_is_rejected_while_the_parent_document_export_is_running()
    {
        var parent = Job("Ready", 1);
        var page = Job("Ready", 1);
        page.ParentId = parent.Id;
        var repo = new RetryRepository([page, parent],
            new ScoreRevision { JobId = page.Id, Version = 1, State = "Ready", Snapshot = "{}" },
            new ScoreRevision { JobId = parent.Id, Version = 1, State = "Processing", Snapshot = "{}" });
        var service = new ScoreService(repo, new Storage());

        var error = await Assert.ThrowsAsync<RequestFailure>(() => service.RetryAsync(page.Id, Token, CancellationToken.None));

        Assert.Equal("export-in-progress", error.Code);
        Assert.Equal("Ready", page.State);
    }

    [Fact]
    public async Task Queued_page_cannot_be_retried()
    {
        var job = Job("Queued", 0);
        var repo = new RetryRepository([job]);
        var service = new ScoreService(repo, new Storage());

        var error = await Assert.ThrowsAsync<RequestFailure>(() => service.RetryAsync(job.Id, Token, CancellationToken.None));

        Assert.Equal("not-retryable", error.Code);
    }

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

    private sealed class RetryRepository(ScoreJob[] jobs, params ScoreRevision[] revisions) : IScoreRepository
    {
        public List<ScoreJob> Jobs { get; } = [.. jobs];
        public List<ScoreRevision> Revisions { get; } = [.. revisions];

        public Task<ScoreJob?> FindJobAsync(Guid id, CancellationToken ct) => Task.FromResult(Jobs.SingleOrDefault(j => j.Id == id));
        public Task<ScoreJob?> FindRequestAsync(string hash, CancellationToken ct) => Task.FromResult<ScoreJob?>(null);
        public Task<ScoreRevision?> FindRevisionAsync(Guid id, int version, CancellationToken ct) => Task.FromResult(Revisions.SingleOrDefault(r => r.JobId == id && r.Version == version));
        public Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct) => Task.FromResult(Jobs.Where(j => j.ParentId == parentId).ToArray());
        public Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct) => Task.FromResult(Array.Empty<PageJobView>());
        public void Add(ScoreJob job) => Jobs.Add(job);
        public void Add(ScoreRevision revision) => Revisions.Add(revision);
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
        public void Clear() { }
    }
}
