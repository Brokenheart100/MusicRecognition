using Microsoft.Extensions.Logging.Abstractions;
using MusicRecognition.Infrastructure;
using Xunit;

public sealed class ToolTimeoutTests
{
    [Fact, Trait("Category","Integration")]
    public async Task Real_recognition_timeout_kills_child_tree_and_does_not_publish_raw_output()
    {
        var root=Environment.GetEnvironmentVariable("MUSICRECOGNITION_TEST_ROOT") ?? throw new InvalidOperationException("Set MUSICRECOGNITION_TEST_ROOT for real-tool integration tests.");
        var python=Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? "python";
        var options=new RecognitionOptions {RepositoryRoot=root,DataRoot=Path.Combine(root,"output","p1","timeout"),Python=python,TimeoutSeconds=10};
        var files=new FileStorage(options);var id=Guid.NewGuid();Directory.CreateDirectory(files.JobDirectory(id));
        File.Copy(Path.Combine(root,"output","poc-pdf","input","Minuet-xpose-a4.pdf"),Path.Combine(files.JobDirectory(id),"source.pdf"));
        var engine=new ToolEngine(options,files,NullLogger<ToolEngine>.Instance);
        var error=await Assert.ThrowsAsync<ToolFailure>(()=>engine.RecognizeAsync(id,".pdf",CancellationToken.None));
        Assert.Equal("processing-timeout",error.Code);
        Assert.False(Directory.Exists(Path.Combine(files.JobDirectory(id),"raw")));
        await File.WriteAllTextAsync(Path.Combine(options.DataRoot,"evidence.json"),$"{{\"scoreId\":\"{id}\",\"errorCode\":\"{error.Code}\",\"timeoutSeconds\":10,\"rawPublished\":false}}");
    }
}
