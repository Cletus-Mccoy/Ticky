using System.Security.Cryptography;
using System.Text;

namespace Ticky.Internal.Services;

public class ApiTokenService
{
    public const string TOKEN_PREFIX = "tky_";
    private const int DISPLAY_PREFIX_LENGTH = 12;
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<DataContext> _dbContextFactory;

    public ApiTokenService(IDbContextFactory<DataContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Creates a token and returns its plaintext, which is not stored and cannot be retrieved again.
    /// </summary>
    public async Task<ServiceResult<(ApiToken Token, string Plaintext)>> CreateAsync(
        int userId,
        string name,
        int? boardId = null,
        DateTime? expiresAt = null
    )
    {
        name = name.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<(ApiToken, string)>.Fail(ServiceError.Invalid, "Token name must not be empty.");

        if (expiresAt is not null && expiresAt <= DateTime.Now)
            return ServiceResult<(ApiToken, string)>.Fail(ServiceError.Invalid, "Expiry must be in the future.");

        using var db = _dbContextFactory.CreateDbContext();

        if (boardId is not null && !await db.AccessibleBoards(new Actor(userId)).AnyAsync(x => x.Id == boardId))
            return ServiceResult<(ApiToken, string)>.Fail(ServiceError.NotFound, $"Board {boardId} not found.");

        var plaintext = TOKEN_PREFIX + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var token = new ApiToken
        {
            Name = name,
            TokenHash = Hash(plaintext),
            Prefix = plaintext[..DISPLAY_PREFIX_LENGTH],
            UserId = userId,
            BoardId = boardId,
            ExpiresAt = expiresAt,
        };

        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        return ServiceResult<(ApiToken, string)>.Ok((token, plaintext));
    }

    public async Task<List<ApiToken>> ListAsync(int userId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        return await db.ApiTokens
            .Include(x => x.Board)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<bool> RevokeAsync(int userId, int tokenId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        return await db.ApiTokens
            .Where(x => x.Id == tokenId && x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, DateTime.Now)) > 0;
    }

    /// <summary>
    /// Resolves a presented token to the actor it acts as, or null if it is unknown, revoked or expired.
    /// </summary>
    public async Task<Actor?> ValidateAsync(string plaintext)
    {
        if (!plaintext.StartsWith(TOKEN_PREFIX, StringComparison.Ordinal))
            return null;

        var hash = Hash(plaintext);
        var now = DateTime.Now;

        using var db = _dbContextFactory.CreateDbContext();

        var token = await db.ApiTokens
            .Where(x => x.TokenHash == hash && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now))
            .Select(x => new { x.Id, x.UserId, x.BoardId, x.LastUsedAt })
            .FirstOrDefaultAsync();

        if (token is null)
            return null;

        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedResolution)
        {
            await db.ApiTokens
                .Where(x => x.Id == token.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.LastUsedAt, now));
        }

        return new Actor(token.UserId, token.BoardId);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
