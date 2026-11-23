using System.Security.Cryptography;
using System.Text;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;

namespace MusicRecognition.Application.Authorization;

internal static class ScoreAccess
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string TokenHash(string token)
    {
        if (token.Length is < 32 or > 200)
            throw new RequestFailure(401, "invalid-token", "Supply X-Score-Token with at least 32 random characters.");
        return Hash(token);
    }

    public static async Task<ScoreJob> AuthorizeAsync(IScoreRepository db, Guid id, string token, CancellationToken ct)
    {
        var hash = TokenHash(token);
        var job = await db.FindJobAsync(id, ct);
        if (job is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(job.TokenHash)))
            throw new RequestFailure(404, "not-found", "Score not found.");
        return job;
    }
}
