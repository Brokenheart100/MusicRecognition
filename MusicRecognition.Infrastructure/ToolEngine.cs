using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Infrastructure;

public sealed class ToolFailure(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

public sealed class ToolEngine(RecognitionOptions options, IScoreStorage files, ILogger<ToolEngine> logger) : IRecognitionEngine
{
    private async Task Run(string operation, Guid id, string[] args, CancellationToken ct)
    {
        var dir=files.JobDirectory(id); var logPath=Path.Combine(dir,$"{operation}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var start=new ProcessStartInfo(options.Python) {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=options.RepositoryRoot,CreateNoWindow=true};
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(options.RepositoryRoot,"scripts","runtime","pipeline.py"));
        start.ArgumentList.Add(operation); start.ArgumentList.Add("--root"); start.ArgumentList.Add(options.RepositoryRoot);
        start.ArgumentList.Add("--job"); start.ArgumentList.Add(dir);
        foreach(var arg in args) start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"]="utf-8";
        using var process=new Process {StartInfo=start};
        await using var log=new StreamWriter(logPath);
        var logGate=new SemaphoreSlim(1); long lines=0;
        async Task Drain(StreamReader reader) {while(await reader.ReadLineAsync() is { } line) {if(Interlocked.Increment(ref lines)>10000) continue; await logGate.WaitAsync(); try {await log.WriteLineAsync(line[..Math.Min(line.Length,4000)]);} finally {logGate.Release();}}}
        try
        {
            process.Start(); logger.LogInformation("Score {ScoreId}: starting {Operation}",id,operation);
            var stdout=Drain(process.StandardOutput); var stderr=Drain(process.StandardError);
            try {await process.WaitForExitAsync(timeout.Token);}
            finally {if(!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout,stderr);}
            if(process.ExitCode!=0)
            {
                var errorFile=Path.Combine(dir,"error.json");
                var code=File.Exists(errorFile)? JsonDocument.Parse(await File.ReadAllTextAsync(errorFile,ct)).RootElement.GetProperty("code").GetString()! : "tool-failed";
                throw new ToolFailure(code,$"{operation} failed; consult the protected server logs for score {id}.");
            }
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) {throw new ToolFailure("processing-timeout",$"{operation} exceeded {options.TimeoutSeconds} seconds.");}
    }
    public async Task<ScoreSnapshot> RecognizeAsync(Guid id,string extension,CancellationToken ct)
    {
        await Run("recognize",id,["--extension",extension],ct);
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(files.JobDirectory(id),"raw","notes-1.json"),ct));
        var root=document.RootElement;
        var notes=root.GetProperty("notes").EnumerateArray().Select(n=>
        {
            var pitch=new Pitch(Enum.Parse<Step>(n.GetProperty("step").GetString()!),n.GetProperty("alter").GetInt32(),n.GetProperty("octave").GetInt32());
            var b=n.GetProperty("bounds").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
            return new Note(Guid.NewGuid(),n.GetProperty("interId").GetInt32(),n.GetProperty("measure").GetInt32(),n.GetProperty("part").GetInt32(),
                n.GetProperty("staff").GetInt32(),n.GetProperty("voice").GetInt32(),pitch,new(b[0],b[1],b[2],b[3]),Numbering.Convert(pitch,n.GetProperty("measure").GetInt32(),NumberingOptions.FixedC));
        }).ToArray();
        if(notes.Length==0 || notes.Length!=root.GetProperty("headCount").GetInt32()) throw new ToolFailure("invalid-note-count","No complete note set was produced.");
        return new(root.GetProperty("width").GetInt32(),root.GetProperty("height").GetInt32(),root.GetProperty("interline").GetInt32(),root.GetProperty("engine").GetString()!,NumberingOptions.FixedC,notes,
            root.GetProperty("warnings").EnumerateArray().Select(v=>v.GetString()!).ToArray());
    }
    public async Task RenderAsync(Guid id,int version,string extension,ScoreSnapshot snapshot,CancellationToken ct)
    {
        var labels=Path.Combine(files.JobDirectory(id),$"labels-{version}.json");
        await File.WriteAllTextAsync(labels,ScoreJson.Write(snapshot.Notes.ToDictionary(n=>n.InterId.ToString(),n=>new {noteId=n.Id,pitch=n.Pitch,degree=n.Label.Degree,alteration=n.Label.Alteration,octaveDots=n.Label.OctaveDots})),ct);
        await Run("render",id,["--extension",extension,"--version",version.ToString(),"--labels",labels,"--font",options.Font],ct);
    }
}
