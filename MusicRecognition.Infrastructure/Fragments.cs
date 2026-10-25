using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Infrastructure;

public sealed class FragmentStorage(IScoreStorage files) : IFragmentStorage
{
    public async Task<string> CreateAsync(Guid id,FragmentUpload[] uploads,FragmentPlan plan,CancellationToken ct)
    {
        var dir=files.JobDirectory(id); Directory.CreateDirectory(dir);
        try
        {
        var sources=new List<FragmentSource>(); long total=0;
        for (var i=0;i<uploads.Length;i++)
        {
            var upload=uploads[i]; var ext=Path.GetExtension(upload.FileName).ToLowerInvariant();
            if (ext==".jpeg") ext=".jpg";
            if (ext is not (".png" or ".jpg")) throw new RequestFailure(415,"unsupported-type","片段只支持 PNG、JPG 图片。");
            var relative=$"original-{i}{ext}"; var path=Path.Combine(dir,relative);
            await using (var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write))
            {
                var buffer=new byte[81920]; int read;
                while ((read=await upload.Content.ReadAsync(buffer,ct))>0)
                {
                    total+=read; if (total>20*1024*1024) throw new RequestFailure(413,"file-too-large","片段图片合计不能超过 20 MiB。");
                    await output.WriteAsync(buffer.AsMemory(0,read),ct);
                }
            }
            await using var input=File.OpenRead(path);
            if (input.Length<8) throw new ArgumentException("图片为空或不完整。");
            var signature=new byte[8]; await input.ReadExactlyAsync(signature,ct);
            var valid=ext==".png" ? signature.AsSpan().SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) : signature[0]==255 && signature[1]==216 && signature[2]==255;
            if (!valid) throw new ArgumentException("图片内容与扩展名不一致。");
            input.Position=0; var hash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct));
            sources.Add(new(i,ScoreDiagnostics.SafeFileName(upload.FileName),relative,hash));
        }
        var descriptor=new FragmentInput(sources.ToArray(),plan);
        var json=ScoreJson.Write(descriptor);
        await File.WriteAllTextAsync(Path.Combine(dir,"input.json"),json,ct);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
        catch { DiscardUnpublished(id); throw; }
    }
    public void DiscardUnpublished(Guid id)
    {
        var dir=Path.GetFullPath(files.JobDirectory(id));
        if (!Directory.Exists(dir)) return;
        if (Path.GetFileName(dir)!=id.ToString("N") || (File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("拒绝清理非任务临时目录。");
        var paths=Directory.GetFiles(dir);
        if (Directory.GetDirectories(dir).Length!=0 || paths.Any(p=>Path.GetFileName(p)!="input.json" && !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p),@"^original-\d+\.(png|jpg)$")))
            throw new IOException("拒绝清理已包含处理产物的目录。");
        foreach(var path in paths) File.Delete(path);
        Directory.Delete(dir);
    }
    public async Task<FragmentInput> InputAsync(Guid id,CancellationToken ct) => ScoreJson.Read<FragmentInput>(await File.ReadAllTextAsync(Path.Combine(files.JobDirectory(id),"input.json"),ct));
    public async Task<string> StageAsync(Guid id,CancellationToken ct)
    {
        var path=Path.Combine(files.JobDirectory(id),"progress.json");
        if (!File.Exists(path)) return "等待后台开始拼谱";
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var doc=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
        return doc.RootElement.GetProperty("stage").GetString() ?? "正在拼谱";
    }
    public async Task<FragmentResult?> ResultAsync(Guid id,int version,CancellationToken ct)
    {
        var path=Path.Combine(files.JobDirectory(id),"revisions",version.ToString(),"report.json");
        return File.Exists(path) ? ScoreJson.Read<FragmentResult>(await File.ReadAllTextAsync(path,ct)) : null;
    }
    public StoredFile Artifact(Guid id,int version,string name)
    {
        var type=name switch { "score.musicxml"=>"application/vnd.recordare.musicxml+xml", "score.mxl"=>"application/vnd.recordare.musicxml", "score.pdf" or "source.pdf"=>"application/pdf", "report.json" or "provenance.json"=>"application/json", _=>throw new RequestFailure(404,"not-found","拼谱文件不存在。") };
        var path=Path.Combine(files.JobDirectory(id),"revisions",version.ToString(),name);
        if (!File.Exists(path)) throw new RequestFailure(404,"not-found","拼谱文件不存在。");
        return new(path,type,$"composition-v{version}-{name}");
    }
    public async Task<StoredFile> ImageAsync(Guid id,int index,CancellationToken ct)
    {
        var source=(await InputAsync(id,ct)).Sources.SingleOrDefault(s=>s.Index==index) ?? throw new RequestFailure(404,"not-found","图片不存在。");
        return new(Path.Combine(files.JobDirectory(id),source.RelativePath),source.RelativePath.EndsWith(".png")?"image/png":"image/jpeg",source.FileName);
    }
}
public sealed class FragmentEngine(ToolEngine tools,IScoreStorage files) : IFragmentEngine
{
    public async Task ComposeAsync(Guid id,int version,FragmentPlan plan,CancellationToken ct)
    {
        var dir=files.JobDirectory(id);
        await File.WriteAllTextAsync(Path.Combine(dir,$"run-{version}.json"),ScoreJson.Write(plan),ct);
        await tools.Run("compose",id,["--version",version.ToString()],ct,"fragments.py");
    }
}
