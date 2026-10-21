using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddRecognition(this IServiceCollection services,IConfiguration config,bool worker)
    {
        var options=config.GetSection("Recognition").Get<RecognitionOptions>() ?? new();
        if(string.IsNullOrWhiteSpace(options.DataRoot) || string.IsNullOrWhiteSpace(options.RepositoryRoot) || options.TimeoutSeconds is < 10 or > 1800)
            throw new InvalidOperationException("Configure Recognition RepositoryRoot, DataRoot and TimeoutSeconds (10..1800).");
        services.AddSingleton(options); services.AddSingleton<IScoreStorage,FileStorage>();
        var connection=config.GetConnectionString("recognitiondb") ?? throw new InvalidOperationException("Missing recognitiondb connection string.");
        services.AddDbContext<ScoreDb>(o=>o.UseNpgsql(connection));
        services.AddHealthChecks().AddCheck<DatabaseHealth>("recognitiondb");
        services.AddScoped<IScoreRepository,ScoreRepository>();services.AddScoped<IScoreService,ScoreService>(); services.AddScoped<IRecognitionEngine,ToolEngine>(); services.AddScoped<ScoreProcessor>();
        services.AddHangfire(h=>h.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c=>c.UseNpgsqlConnection(connection),new PostgreSqlStorageOptions {InvisibilityTimeout=TimeSpan.FromMinutes(1),QueuePollInterval=TimeSpan.FromSeconds(2)}));
        if(worker)
        {
            services.AddHangfireServer(o=> {o.WorkerCount=1; o.Queues=["recognition","render"];o.CancellationCheckInterval=TimeSpan.FromSeconds(1);o.ShutdownTimeout=TimeSpan.FromSeconds(10);});
            services.AddHostedService<DispatchService>();
        }
        return services;
    }
    public static async Task MigrateRecognitionAsync(this IServiceProvider provider,CancellationToken ct=default)
    {await using var scope=provider.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.MigrateAsync(ct);}
}

// The domain records form a durable dispatch outbox. Publishing a Hangfire job twice is safe:
// each handler takes the same per-score lock, then checks the persisted terminal state.
public sealed class DispatchService(IServiceScopeFactory scopes,IBackgroundJobClient jobs,JobStorage storage,ILogger<DispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection=storage.GetConnection();
                using var gate=connection.AcquireDistributedLock("music-dispatch",TimeSpan.FromSeconds(5));
                await using var scope=scopes.CreateAsyncScope(); var db=scope.ServiceProvider.GetRequiredService<ScoreDb>();
                foreach(var id in await db.Jobs.Where(j=>j.State=="Queued" && j.BackgroundId==null && !j.CancelRequested).OrderBy(j=>j.CreatedAt).ThenBy(j=>j.Id).Select(j=>j.Id).Take(100).ToListAsync(stoppingToken))
                {
                    var backgroundId=jobs.Enqueue<ScoreProcessor>(p=>p.Recognize(id,CancellationToken.None));
                    // The worker can start before this update. Do not overwrite its state/xmin.
                    await db.Jobs.Where(j=>j.Id==id && j.State=="Queued" && j.BackgroundId==null)
                        .ExecuteUpdateAsync(s=>s.SetProperty(j=>j.BackgroundId,backgroundId),stoppingToken);
                }
                foreach(var revision in await db.Revisions.AsNoTracking().Where(r=>r.State=="Queued" && r.BackgroundId==null).OrderBy(r=>r.JobId).ThenBy(r=>r.Version).Take(100).ToListAsync(stoppingToken))
                {
                    var backgroundId=jobs.Enqueue<ScoreProcessor>(p=>p.Render(revision.JobId,revision.Version,CancellationToken.None));
                    await db.Revisions.Where(r=>r.JobId==revision.JobId && r.Version==revision.Version && r.State=="Queued" && r.BackgroundId==null)
                        .ExecuteUpdateAsync(s=>s.SetProperty(r=>r.BackgroundId,backgroundId),stoppingToken);
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {break;}
            catch(Exception e) {logger.LogError(e,"Durable score dispatch failed; retrying");}
            await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken);
        }
    }
}

public sealed class ScoreProcessor(ScoreDb db,IRecognitionEngine engine,JobStorage storage,IServiceScopeFactory scopes,ILogger<ScoreProcessor> logger)
{
    [Queue("recognition"), AutomaticRetry(Attempts=2,DelaysInSeconds=new[]{5,15})]
    public async Task Recognize(Guid id,CancellationToken shutdown)
    {
        using var connection=storage.GetConnection();using var gate=connection.AcquireDistributedLock("score-"+id,TimeSpan.FromMinutes(10));
        var job=await db.Jobs.SingleAsync(j=>j.Id==id,shutdown);
        if(job.CancelRequested)
        {
            if(job.State=="Cancelling") {job.State="Cancelled";job.UpdatedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(shutdown);}
            return;
        }
        if(job.State=="Ready") return;
        job.State="Processing";job.Attempts++;job.ErrorCode=null;job.ErrorMessage=null;job.UpdatedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(shutdown);
        using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var watcher=WatchCancellation(id,cancelled);
        try
        {
            var snapshot=await engine.RecognizeAsync(id,job.Extension,cancelled.Token);
            await db.Entry(job).ReloadAsync(cancelled.Token);
            if(job.CancelRequested) return;
            job.CurrentRevision=1;job.State="Ready";job.UpdatedAt=DateTimeOffset.UtcNow;
            db.Revisions.Add(new ScoreRevision {JobId=id,Version=1,Snapshot=ScoreJson.Write(snapshot)});
            await db.SaveChangesAsync(cancelled.Token);
        }
        catch(OperationCanceledException)
        {
            db.ChangeTracker.Clear();job=await db.Jobs.SingleAsync(j=>j.Id==id);
            job.State=job.CancelRequested?"Cancelled":"Queued";job.BackgroundId=null;job.UpdatedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();
            if(shutdown.IsCancellationRequested) throw;
        }
        catch(Exception e)
        {
            db.ChangeTracker.Clear();job=await db.Jobs.SingleAsync(j=>j.Id==id);
            if(!job.CancelRequested) {job.State="Failed";job.ErrorCode=e is ToolFailure t?t.Code:"recognition-failed";job.ErrorMessage=e is ToolFailure?e.Message:"Recognition failed. Consult the server logs using this score ID.";job.UpdatedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
            logger.LogError(e,"Recognition failed for {ScoreId}",id);
            // Deterministic failures are user-visible and retried explicitly, not silently looped.
        }
        finally {cancelled.Cancel();try {await watcher;} catch(OperationCanceledException) {}}
    }
    private async Task WatchCancellation(Guid id,CancellationTokenSource stop)
    {
        while(!stop.IsCancellationRequested)
        {
            await Task.Delay(500,stop.Token);
            await using var scope=scopes.CreateAsyncScope();var other=scope.ServiceProvider.GetRequiredService<ScoreDb>();
            if(await other.Jobs.AnyAsync(j=>j.Id==id && j.CancelRequested,stop.Token)) {stop.Cancel();return;}
        }
    }
    [Queue("render"), AutomaticRetry(Attempts=2,DelaysInSeconds=new[]{5,15})]
    public async Task Render(Guid id,int version,CancellationToken shutdown)
    {
        using var connection=storage.GetConnection();using var gate=connection.AcquireDistributedLock("score-"+id,TimeSpan.FromMinutes(10));
        var r=await db.Revisions.FindAsync([id,version],shutdown);
        if(r is null || r.State=="Ready") return;
        var job=await db.Jobs.AsNoTracking().SingleAsync(j=>j.Id==id,shutdown);
        r.State="Processing";await db.SaveChangesAsync(shutdown);
        try {await engine.RenderAsync(id,version,job.Extension,ScoreJson.Read<ScoreSnapshot>(r.Snapshot),shutdown);r.State="Ready";r.ErrorCode=null;await db.SaveChangesAsync(shutdown);}
        catch(OperationCanceledException) when(shutdown.IsCancellationRequested) {r.State="Queued";r.BackgroundId=null;await db.SaveChangesAsync();throw;}
        catch(Exception e) {r.State="Failed";r.ErrorCode=e is ToolFailure t?t.Code:"render-failed";await db.SaveChangesAsync();logger.LogError(e,"Export failed for {ScoreId} revision {Revision}",id,version);}
    }
}
