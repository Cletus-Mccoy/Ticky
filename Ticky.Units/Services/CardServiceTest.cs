using Microsoft.EntityFrameworkCore;

namespace Ticky.Units.Services;

public class CardServiceTest
{
    private SqliteDbFixture _db = null!;
    private CardService _service = null!;
    private SqliteDbFixture.Seed _seed = null!;
    private Actor _member = null!;

    [SetUp]
    public void Setup()
    {
        _db = new SqliteDbFixture();
        _seed = _db.SeedBoards(doingMaxCards: 2);
        _service = new CardService(_db, new CardNumberingService(_db));
        _member = new Actor(_seed.Member.Id);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<Card> Create(string name, Column column) =>
        (await _service.CreateAsync(_member, column.Id, name)).Value!;

    private List<Card> CardsIn(Column column)
    {
        using var db = _db.CreateDbContext();
        return db.Cards.Where(x => x.ColumnId == column.Id).OrderBy(x => x.Index).ToList();
    }

    [Test]
    public async Task Create_NumbersCardsPerBoardAndAppendsAtBottom()
    {
        var first = await Create("First", _seed.Todo);
        var second = await Create("Second", _seed.Todo);
        var other = await Create("Other board", _seed.OtherColumn);

        Assert.Multiple(() =>
        {
            Assert.That(first.Number, Is.EqualTo(1));
            Assert.That(second.Number, Is.EqualTo(2));
            Assert.That(other.Number, Is.EqualTo(1));
            Assert.That(CardsIn(_seed.Todo).Select(x => x.Name), Is.EqualTo(new[] { "First", "Second" }));
        });
    }

    [Test]
    public async Task Create_TopPlacementInsertsFirst()
    {
        using (var db = _db.CreateDbContext())
            await db.Columns.Where(x => x.Id == _seed.Todo.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.NewCardPlacement, CardPlacement.Top));

        await Create("Old", _seed.Todo);
        await Create("New", _seed.Todo);

        Assert.That(CardsIn(_seed.Todo).Select(x => x.Name), Is.EqualTo(new[] { "New", "Old" }));
    }

    [Test]
    public async Task Create_RejectsEmptyNameFullColumnAndNoAccess()
    {
        await Create("A", _seed.Doing);
        await Create("B", _seed.Doing);

        var empty = await _service.CreateAsync(_member, _seed.Todo.Id, "   ");
        var full = await _service.CreateAsync(_member, _seed.Doing.Id, "C");
        var outsider = await _service.CreateAsync(new Actor(_seed.Outsider.Id), _seed.Todo.Id, "D");

        Assert.Multiple(() =>
        {
            Assert.That(empty.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(full.Error, Is.EqualTo(ServiceError.ColumnFull));
            Assert.That(outsider.Error, Is.EqualTo(ServiceError.NotFound));
        });
    }

    [Test]
    public async Task Move_RecordsActivityAndKeepsIndicesContiguous()
    {
        var a = await Create("A", _seed.Todo);
        await Create("B", _seed.Todo);
        await Create("C", _seed.Todo);

        var result = await _service.MoveAsync(_member, a.Id, _seed.Done.Id);

        using var db = _db.CreateDbContext();
        var activity = db.Activities.Single(x => x.ActivityType == ActivityType.CardMoved);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(CardsIn(_seed.Todo).Select(x => (x.Name, x.Index)), Is.EqualTo(new[] { ("B", 0), ("C", 1) }));
            Assert.That(CardsIn(_seed.Done).Select(x => (x.Name, x.Index)), Is.EqualTo(new[] { ("A", 0) }));
            Assert.That(activity.CardId, Is.EqualTo(a.Id));
            Assert.That(activity.UserId, Is.EqualTo(_seed.Member.Id));
            Assert.That(activity.FromColumnName, Is.EqualTo("Todo"));
            Assert.That(activity.ToColumnName, Is.EqualTo("Done"));
            Assert.That(activity.ToColumnFinished, Is.True);
        });
    }

    [Test]
    public async Task Move_BeforeCardAndIndexPositionCorrectly()
    {
        var x = await Create("X", _seed.Done);
        await Create("Y", _seed.Done);
        var a = await Create("A", _seed.Todo);
        var b = await Create("B", _seed.Todo);

        await _service.MoveAsync(_member, a.Id, _seed.Done.Id, beforeCardId: x.Id);
        await _service.MoveAsync(_member, b.Id, _seed.Done.Id, index: 1);

        Assert.That(CardsIn(_seed.Done).Select(c => c.Name), Is.EqualTo(new[] { "A", "B", "X", "Y" }));
    }

    [Test]
    public async Task Move_RejectsFullColumnSameColumnOtherBoardAndNoAccess()
    {
        await Create("A", _seed.Doing);
        await Create("B", _seed.Doing);
        var card = await Create("C", _seed.Todo);

        var full = await _service.MoveAsync(_member, card.Id, _seed.Doing.Id);
        var same = await _service.MoveAsync(_member, card.Id, _seed.Todo.Id);
        var crossBoard = await _service.MoveAsync(_member, card.Id, _seed.OtherColumn.Id);
        var outsider = await _service.MoveAsync(new Actor(_seed.Outsider.Id), card.Id, _seed.Done.Id);

        using var db = _db.CreateDbContext();

        Assert.Multiple(() =>
        {
            Assert.That(full.Error, Is.EqualTo(ServiceError.ColumnFull));
            Assert.That(same.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(crossBoard.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(outsider.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(db.Cards.Single(x => x.Id == card.Id).ColumnId, Is.EqualTo(_seed.Todo.Id));
            Assert.That(db.Activities.Any(x => x.ActivityType == ActivityType.CardMoved), Is.False);
        });
    }

    [Test]
    public async Task BoardScopedActor_CannotTouchOtherBoards()
    {
        var card = await Create("A", _seed.Todo);
        var scopedToOther = new Actor(_seed.Member.Id, _seed.OtherBoard.Id);

        var move = await _service.MoveAsync(scopedToOther, card.Id, _seed.Done.Id);
        var get = await _service.GetAsync(scopedToOther, card.Id);
        var create = await _service.CreateAsync(scopedToOther, _seed.Todo.Id, "Nope");
        var allowed = await _service.CreateAsync(scopedToOther, _seed.OtherColumn.Id, "Fine");

        Assert.Multiple(() =>
        {
            Assert.That(move.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(get, Is.Null);
            Assert.That(create.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(allowed.Succeeded, Is.True);
        });
    }

    [Test]
    public async Task Update_LogsOnlyChangedFields()
    {
        var card = await Create("A", _seed.Todo);

        await _service.UpdateAsync(_member, card.Id, name: "A");
        await _service.UpdateAsync(_member, card.Id, name: "Renamed", description: "Details");
        var empty = await _service.UpdateAsync(_member, card.Id, name: " ");

        using var db = _db.CreateDbContext();
        var types = db.Activities.Select(x => x.ActivityType).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(types, Is.EquivalentTo(new[] { ActivityType.TitleChanged, ActivityType.DescriptionChanged }));
            Assert.That(db.Cards.Single().Name, Is.EqualTo("Renamed"));
            Assert.That(empty.Error, Is.EqualTo(ServiceError.Invalid));
        });
    }

    [Test]
    public async Task AddComment_StoresCommentAndActivity()
    {
        var card = await Create("A", _seed.Todo);

        var result = await _service.AddCommentAsync(_member, card.Id, "Hello");
        var dto = await _service.GetAsync(_member, card.Id);

        using var db = _db.CreateDbContext();

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(dto!.Comments.Single().Text, Is.EqualTo("Hello"));
            Assert.That(dto.Comments.Single().Author, Is.EqualTo("Member"));
            Assert.That(db.Activities.Single().ActivityType, Is.EqualTo(ActivityType.CommentPosted));
        });
    }

    [Test]
    public async Task GetByKey_ResolvesBoardCodeAndNumber()
    {
        await Create("First", _seed.Todo);
        var second = await Create("Second", _seed.Todo);

        var byKey = await _service.GetByKeyAsync(_member, "tst-2");
        var missing = await _service.GetByKeyAsync(_member, "TST-99");
        var garbage = await _service.GetByKeyAsync(_member, "nonsense");

        Assert.Multiple(() =>
        {
            Assert.That(byKey!.Id, Is.EqualTo(second.Id));
            Assert.That(byKey.Key, Is.EqualTo("TST-2"));
            Assert.That(byKey.ColumnName, Is.EqualTo("Todo"));
            Assert.That(missing, Is.Null);
            Assert.That(garbage, Is.Null);
        });
    }
}
