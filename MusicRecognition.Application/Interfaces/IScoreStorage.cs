namespace MusicRecognition.Application.Interfaces;

// 文件存储接口（storage port）：应用层不关心文件放在哪，只通过它保存原稿、定位任务目录。
// 每个任务（job）有一个以任务 ID 命名的目录，原稿和全部产物都放在里面，方便按任务清理。
// 实现放在基础设施层，负责防路径穿越、按文件头判断真实类型等安全细节。
// DiscardSource 用于重复上传时删掉刚存的多余原稿。
public interface IScoreStorage
{
    string JobDirectory(Guid id);
    Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct);
    void DiscardSource(Guid id,string extension);
}
