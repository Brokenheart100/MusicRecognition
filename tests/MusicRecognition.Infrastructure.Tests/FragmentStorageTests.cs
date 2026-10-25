using Microsoft.Extensions.Logging.Abstractions;
using MusicRecognition.Application;
using MusicRecognition.Domain;
using MusicRecognition.Infrastructure;
using Xunit;

public sealed class FragmentStorageTests
{
    [Fact]
    public async Task Invalid_uploads_remove_partial_source_files_and_directory()
    {
        var root=Path.Combine(Path.GetTempPath(),"musicrecognition-upload-"+Guid.NewGuid().ToString("N"));
        var storage=new FileStorage(new() {DataRoot=root},NullLogger<FileStorage>.Instance);
        var fragments=new FragmentStorage(storage);var id=Guid.NewGuid();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(()=>fragments.CreateAsync(id,[new("bad.png",new MemoryStream("not an image"u8.ToArray()))],new("Test",[new(0,null)]),CancellationToken.None));
            Assert.False(Directory.Exists(storage.JobDirectory(id)));
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [Fact]
    public void Unpublished_cleanup_refuses_processed_artifacts()
    {
        var root=Path.Combine(Path.GetTempPath(),"musicrecognition-upload-"+Guid.NewGuid().ToString("N"));
        var storage=new FileStorage(new() {DataRoot=root},NullLogger<FileStorage>.Instance);
        var fragments=new FragmentStorage(storage);var id=Guid.NewGuid();
        var dir=storage.JobDirectory(id);Directory.CreateDirectory(Path.Combine(dir,"revisions"));
        try { Assert.Throws<IOException>(()=>fragments.DiscardUnpublished(id));Assert.True(Directory.Exists(dir)); }
        finally { Directory.Delete(root,true); }
    }
}
