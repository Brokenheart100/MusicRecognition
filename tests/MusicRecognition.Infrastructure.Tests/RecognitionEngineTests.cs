using MusicRecognition.Application;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Handlers;
using MusicRecognition.Application.Interfaces;
using Xunit;

public sealed class RecognitionEngineTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Default_remains_audiveris_and_engine_is_part_of_idempotency()
    {
        var repo = new MemoryRepository(); var files = new SourceStorage();
        var service = new ScoreService(repo, files);
        var first = await service.ImportAsync(Stream.Null, "piano.pdf", Token, "request-one", CancellationToken.None);
        Assert.Equal(RecognitionEngineKind.Audiveris, first.Engine);
        var same = await service.ImportAsync(Stream.Null, "piano.pdf", Token, "request-one", CancellationToken.None);
        Assert.Equal(first.Id, same.Id);
        var conflict = await Assert.ThrowsAsync<RequestFailure>(() => service.ImportAsync(Stream.Null, "piano.pdf", Token, "request-one", CancellationToken.None, engine: RecognitionEngineKind.Homr));
        Assert.Equal(409, conflict.Status);
        var homr = await service.ImportAsync(Stream.Null, "piano.pdf", Token, "request-two", CancellationToken.None, engine: RecognitionEngineKind.Homr);
        Assert.Equal(RecognitionEngineKind.Homr, homr.Engine);
        Assert.Equal(RecognitionEngineKind.Homr, repo.Jobs.Single(j => j.Id == homr.Id).Engine);
    }

    [Fact]
    public async Task Undefined_engine_is_rejected_before_source_storage()
    {
        var files = new SourceStorage();
        var service = new ScoreService(new MemoryRepository(), files);
        var error = await Assert.ThrowsAsync<RequestFailure>(() => service.ImportAsync(Stream.Null, "piano.pdf", Token, "request-one", CancellationToken.None, engine: (RecognitionEngineKind)999));
        Assert.Equal("invalid-engine", error.Code);
        Assert.Equal(0, files.Saves);
    }

    private sealed class SourceStorage : IScoreStorage
    {
        public int Saves;
        public string JobDirectory(Guid id) => throw new NotSupportedException();
        public void DiscardSource(Guid id, string extension) { }
        public Task<(string Extension,string Hash)> SaveSourceAsync(Guid id,Stream content,string name,CancellationToken ct)
        { Saves++; return Task.FromResult((".pdf","same-file-hash")); }
    }
    private sealed class MemoryRepository : IScoreRepository
    {
        public List<ScoreJob> Jobs { get; } = [];
        public Task<ScoreJob?> FindJobAsync(Guid id,CancellationToken ct) => Task.FromResult(Jobs.SingleOrDefault(j=>j.Id==id));
        public Task<ScoreJob?> FindRequestAsync(string hash,CancellationToken ct) => Task.FromResult(Jobs.SingleOrDefault(j=>j.RequestHash==hash));
        public Task<ScoreRevision?> FindRevisionAsync(Guid id,int version,CancellationToken ct) => throw new NotSupportedException();
        public Task<ScoreJob[]> FindPagesAsync(Guid id,CancellationToken ct) => throw new NotSupportedException();
        public Task<PageJobView[]> PageViewsAsync(Guid id,CancellationToken ct) => throw new NotSupportedException();
        public void Add(ScoreJob job) => Jobs.Add(job);
        public void Add(ScoreRevision revision) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
        public void Clear() { }
    }
}
