using Microsoft.EntityFrameworkCore;

namespace Ticky.Units.Services;

public class ApiTokenServiceTest
{
    private SqliteDbFixture _db = null!;
    private SqliteDbFixture.Seed _seed = null!;
    private ApiTokenService _service = null!;

    [SetUp]
    public void Setup()
    {
        _db = new SqliteDbFixture();
        _seed = _db.SeedBoards();
        _service = new ApiTokenService(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task Create_StoresOnlyHashAndValidatesToActor()
    {
        var result = await _service.CreateAsync(_seed.Member.Id, "agents");
        var (token, plaintext) = result.Value;

        var actor = await _service.ValidateAsync(plaintext);

        using var db = _db.CreateDbContext();
        var stored = db.ApiTokens.Single();

        Assert.Multiple(() =>
        {
            Assert.That(plaintext, Does.StartWith(ApiTokenService.TOKEN_PREFIX));
            Assert.That(stored.TokenHash, Is.EqualTo(ApiTokenService.Hash(plaintext)));
            Assert.That(stored.TokenHash, Does.Not.Contain(plaintext[ApiTokenService.TOKEN_PREFIX.Length..]));
            Assert.That(stored.Prefix, Is.EqualTo(plaintext[..stored.Prefix.Length]));
            Assert.That(actor, Is.EqualTo(new Actor(_seed.Member.Id)));
            Assert.That(stored.LastUsedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Create_BoardScopeCarriesIntoActorAndRequiresAccess()
    {
        var scoped = await _service.CreateAsync(_seed.Member.Id, "one board", _seed.Board.Id);
        var denied = await _service.CreateAsync(_seed.Outsider.Id, "sneaky", _seed.Board.Id);

        var actor = await _service.ValidateAsync(scoped.Value.Plaintext);

        Assert.Multiple(() =>
        {
            Assert.That(actor, Is.EqualTo(new Actor(_seed.Member.Id, _seed.Board.Id)));
            Assert.That(denied.Error, Is.EqualTo(ServiceError.NotFound));
        });
    }

    [Test]
    public async Task Validate_RejectsUnknownRevokedAndExpiredTokens()
    {
        var revoked = (await _service.CreateAsync(_seed.Member.Id, "revoked")).Value;
        var expiring = (await _service.CreateAsync(_seed.Member.Id, "expiring", expiresAt: DateTime.Now.AddDays(1))).Value;

        await _service.RevokeAsync(_seed.Member.Id, revoked.Token.Id);

        using (var db = _db.CreateDbContext())
            await db.ApiTokens.Where(x => x.Id == expiring.Token.Id).ExecuteUpdateAsync(x => x.SetProperty(t => t.ExpiresAt, DateTime.Now.AddMinutes(-1)));

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.ValidateAsync(revoked.Plaintext), Is.Null);
            Assert.That(await _service.ValidateAsync(expiring.Plaintext), Is.Null);
            Assert.That(await _service.ValidateAsync("tky_doesnotexist"), Is.Null);
            Assert.That(await _service.ValidateAsync("not-a-token"), Is.Null);
        });
    }

    [Test]
    public async Task Revoke_OnlyAffectsOwnTokens()
    {
        var token = (await _service.CreateAsync(_seed.Member.Id, "mine")).Value;

        var byOther = await _service.RevokeAsync(_seed.Outsider.Id, token.Token.Id);
        var byOwner = await _service.RevokeAsync(_seed.Member.Id, token.Token.Id);
        var again = await _service.RevokeAsync(_seed.Member.Id, token.Token.Id);

        Assert.Multiple(() =>
        {
            Assert.That(byOther, Is.False);
            Assert.That(byOwner, Is.True);
            Assert.That(again, Is.False);
        });
    }

    [Test]
    public async Task Create_RejectsEmptyNameAndPastExpiry()
    {
        var empty = await _service.CreateAsync(_seed.Member.Id, "  ");
        var past = await _service.CreateAsync(_seed.Member.Id, "old", expiresAt: DateTime.Now.AddDays(-1));

        Assert.Multiple(() =>
        {
            Assert.That(empty.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(past.Error, Is.EqualTo(ServiceError.Invalid));
        });
    }

    [Test]
    public async Task DeletingScopedBoard_DeletesTokenInsteadOfWideningIt()
    {
        var token = (await _service.CreateAsync(_seed.Member.Id, "scoped", _seed.OtherBoard.Id)).Value;

        using (var db = _db.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            await db.Columns.Where(x => x.BoardId == _seed.OtherBoard.Id).ExecuteDeleteAsync();
            await db.BoardMemberships.Where(x => x.BoardId == _seed.OtherBoard.Id).ExecuteDeleteAsync();
            await db.Boards.Where(x => x.Id == _seed.OtherBoard.Id).ExecuteDeleteAsync();
        }

        Assert.That(await _service.ValidateAsync(token.Plaintext), Is.Null);
    }
}
