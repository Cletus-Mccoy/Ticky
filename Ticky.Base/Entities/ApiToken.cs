namespace Ticky.Base.Entities;

/// <summary>
/// Personal access token for the REST API and MCP endpoint. Only a SHA-256 hash of the token is stored;
/// the plaintext is shown to the user once at creation.
/// </summary>
public class ApiToken : AbstractDbEntity
{
    public required string Name { get; set; }
    public required string TokenHash { get; set; }

    /// <summary>
    /// First characters of the token, shown in the UI so users can tell tokens apart.
    /// </summary>
    public required string Prefix { get; set; }

    public required int UserId { get; set; }
    public virtual User User { get; set; } = null!;

    /// <summary>
    /// When set, the token can only access this board.
    /// </summary>
    public int? BoardId { get; set; }
    public virtual Board? Board { get; set; }

    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
