using System.Security.Cryptography;
using System.Text;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;

namespace MusicRecognition.Application.Authorization;

// 乐谱访问校验（access check）：每个接口都要带一个访问令牌（token），这里负责验证它。
// 数据库只存令牌的哈希（TokenHash），不存明文；比对用固定耗时算法，防止计时攻击。
// 校验失败统一报"找不到"（404），不透露"这个乐谱存在但令牌不对"。
internal static class ScoreAccess
{
    // SHA-256 哈希：把任意文本变成固定长度指纹，数据库里只出现指纹。
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // 先检查长度 32~200：32 是 128 位随机串的常见长度，太短容易被猜，太长没有意义。
    public static string TokenHash(string token)
    {
        if (token.Length is < 32 or > 200)
            throw new RequestFailure(401, "invalid-token", "Supply X-Score-Token with at least 32 random characters.");
        return Hash(token);
    }

    // 按令牌哈希取任务；令牌不对和任务不存在都按 404 处理，调用方不必区分。
    public static async Task<ScoreJob> AuthorizeAsync(IScoreRepository db, Guid id, string token, CancellationToken ct)
    {
        // FixedTimeEquals 逐字节比较且耗时恒定，避免攻击者靠响应时间猜出令牌。
        var hash = TokenHash(token);
        var job = await db.FindJobAsync(id, ct);
        if (job is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(job.TokenHash)))
            throw new RequestFailure(404, "not-found", "Score not found.");
        return job;
    }
}
