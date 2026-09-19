using System.Drawing;

namespace Ticky.Units.Services;

public class BoardServiceColumnTest
{
    private SqliteDbFixture _db = null!;
    private SqliteDbFixture.Seed _seed = null!;
    private BoardService _boards = null!;

    [SetUp]
    public void Setup()
    {
        _db = new SqliteDbFixture();
        _seed = _db.SeedBoards();
        _boards = new BoardService(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task CreateColumn_ProjectAdminAppendsColumnWithSettings()
    {
        var result = await _boards.CreateColumnAsync(new Actor(_seed.Member.Id), _seed.Board.Id, " Review ", 3, true, CardPlacement.Top);

        var board = await _boards.GetAsync(new Actor(_seed.Member.Id), _seed.Board.Id);
        var created = board!.Columns.Last();

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(board.Columns.Select(x => x.Name), Is.EqualTo(new[] { "Todo", "Doing", "Done", "Review" }));
            Assert.That((created.Index, created.MaxCards, created.Finished), Is.EqualTo((3, 3, true)));
            Assert.That(result.Value!.NewCardPlacement, Is.EqualTo(CardPlacement.Top));
        });
    }

    [Test]
    public async Task CreateColumn_NonAdminMemberIsForbidden()
    {
        // Member is a plain (non-admin) board member of OtherBoard
        var result = await _boards.CreateColumnAsync(new Actor(_seed.Member.Id), _seed.OtherBoard.Id, "Nope");

        Assert.That(result.Error, Is.EqualTo(ServiceError.Forbidden));
    }

    [Test]
    public async Task CreateColumn_BoardMembershipTakesPrecedenceOverProjectAdmin()
    {
        // Same rule as BoardView.IsAdmin: a board membership decides, even if the project membership is admin
        using (var db = _db.CreateDbContext())
        {
            db.BoardMemberships.Add(new BoardMembership { BoardId = _seed.Board.Id, UserId = _seed.Member.Id, IsAdmin = false, AddedAt = DateTime.Now });
            db.SaveChanges();
        }

        var result = await _boards.CreateColumnAsync(new Actor(_seed.Member.Id), _seed.Board.Id, "Nope");

        Assert.That(result.Error, Is.EqualTo(ServiceError.Forbidden));
    }

    [Test]
    public async Task CreateColumn_RejectsOutsiderAndInvalidInput()
    {
        var outsider = await _boards.CreateColumnAsync(new Actor(_seed.Outsider.Id), _seed.Board.Id, "X");
        var empty = await _boards.CreateColumnAsync(new Actor(_seed.Member.Id), _seed.Board.Id, " ");
        var negative = await _boards.CreateColumnAsync(new Actor(_seed.Member.Id), _seed.Board.Id, "X", maxCards: -1);

        Assert.Multiple(() =>
        {
            Assert.That(outsider.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(empty.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(negative.Error, Is.EqualTo(ServiceError.Invalid));
        });
    }

    [Test]
    public async Task Labels_ListedPerBoardAndIncludedInBoard()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Labels.AddRange(
                new Label { Name = "bug", BoardId = _seed.Board.Id, TextColor = Color.White, BackgroundColor = Color.Red },
                new Label { Name = "api", BoardId = _seed.Board.Id, TextColor = Color.White, BackgroundColor = Color.Blue },
                new Label { Name = "other", BoardId = _seed.OtherBoard.Id, TextColor = Color.White, BackgroundColor = Color.Blue }
            );
            db.SaveChanges();
        }

        var labels = await _boards.GetLabelsAsync(new Actor(_seed.Member.Id), _seed.Board.Id);
        var board = await _boards.GetAsync(new Actor(_seed.Member.Id), _seed.Board.Id);
        var denied = await _boards.GetLabelsAsync(new Actor(_seed.Outsider.Id), _seed.Board.Id);

        Assert.Multiple(() =>
        {
            Assert.That(labels!.Select(x => x.Name), Is.EqualTo(new[] { "api", "bug" }));
            Assert.That(board!.Labels.Select(x => x.Name), Is.EqualTo(new[] { "api", "bug" }));
            Assert.That(denied, Is.Null);
        });
    }
}
