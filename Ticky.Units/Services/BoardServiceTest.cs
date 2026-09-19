namespace Ticky.Units.Services;

public class BoardServiceTest
{
    private SqliteDbFixture _db = null!;
    private SqliteDbFixture.Seed _seed = null!;
    private CardService _cards = null!;
    private BoardService _boards = null!;
    private Actor _member = null!;

    [SetUp]
    public void Setup()
    {
        _db = new SqliteDbFixture();
        _seed = _db.SeedBoards();
        _cards = new CardService(_db, new CardNumberingService(_db));
        _boards = new BoardService(_db);
        _member = new Actor(_seed.Member.Id);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task List_ReturnsProjectAndBoardMembershipsRespectingScope()
    {
        var all = await _boards.ListAsync(_member);
        var scoped = await _boards.ListAsync(new Actor(_seed.Member.Id, _seed.Board.Id));
        var outsider = await _boards.ListAsync(new Actor(_seed.Outsider.Id));

        Assert.Multiple(() =>
        {
            Assert.That(all.Select(x => x.Code), Is.EquivalentTo(new[] { "TST", "OTH" }));
            Assert.That(scoped.Select(x => x.Code), Is.EqualTo(new[] { "TST" }));
            Assert.That(outsider, Is.Empty);
        });
    }

    [Test]
    public async Task Get_ReturnsColumnsInOrderWithCards()
    {
        var card = (await _cards.CreateAsync(_member, _seed.Doing.Id, "Work")).Value!;

        var board = await _boards.GetAsync(_member, _seed.Board.Id);
        var denied = await _boards.GetAsync(new Actor(_seed.Outsider.Id), _seed.Board.Id);

        Assert.Multiple(() =>
        {
            Assert.That(board!.Columns.Select(x => x.Name), Is.EqualTo(new[] { "Todo", "Doing", "Done" }));
            Assert.That(board.Columns.Single(x => x.Name == "Doing").CardCount, Is.EqualTo(1));
            Assert.That(board.Cards.Single().Key, Is.EqualTo("TST-" + card.Number));
            Assert.That(denied, Is.Null);
        });
    }

    [Test]
    public async Task Activity_FiltersByTypeAndPagesWithBefore()
    {
        var card = (await _cards.CreateAsync(_member, _seed.Todo.Id, "A")).Value!;
        await _cards.AddCommentAsync(_member, card.Id, "one");
        await Task.Delay(5);
        await _cards.MoveAsync(_member, card.Id, _seed.Doing.Id);
        await Task.Delay(5);
        await _cards.AddCommentAsync(_member, card.Id, "two");

        var all = await _boards.GetActivityAsync(_member, _seed.Board.Id);
        var moves = await _boards.GetActivityAsync(_member, _seed.Board.Id, ActivityType.CardMoved);
        var limited = await _boards.GetActivityAsync(_member, _seed.Board.Id, limit: 1);
        var older = await _boards.GetActivityAsync(_member, _seed.Board.Id, before: all![0].CreatedAt);
        var denied = await _boards.GetActivityAsync(new Actor(_seed.Outsider.Id), _seed.Board.Id);

        Assert.Multiple(() =>
        {
            Assert.That(all.Select(x => x.Type), Is.EqualTo(new[] { ActivityType.CommentPosted, ActivityType.CardMoved, ActivityType.CommentPosted }));
            Assert.That(moves!.Single().ToColumn, Is.EqualTo("Doing"));
            Assert.That(moves!.Single().CardKey, Is.EqualTo("TST-1"));
            Assert.That(limited, Has.Count.EqualTo(1));
            Assert.That(older, Has.Count.EqualTo(2));
            Assert.That(denied, Is.Null);
        });
    }

    [Test]
    public async Task Stats_CountsColumnsAndBurndown()
    {
        var a = (await _cards.CreateAsync(_member, _seed.Todo.Id, "A")).Value!;
        await _cards.CreateAsync(_member, _seed.Todo.Id, "B");
        await _cards.MoveAsync(_member, a.Id, _seed.Done.Id);

        var stats = await _boards.GetStatsAsync(_member, _seed.Board.Id);

        Assert.Multiple(() =>
        {
            Assert.That((stats!.Total, stats.Done, stats.Open), Is.EqualTo((2, 1, 1)));
            Assert.That(stats.Columns.Select(x => x.Cards), Is.EqualTo(new[] { 1, 0, 1 }));
            Assert.That(stats.Burndown[^1].Open, Is.EqualTo(1));
        });
    }
}
