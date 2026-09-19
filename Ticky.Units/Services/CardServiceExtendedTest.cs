using System.Drawing;
using Microsoft.EntityFrameworkCore;

namespace Ticky.Units.Services;

/// <summary>
/// Priority, delete, labels and links: the operations added so API clients never need direct database access.
/// </summary>
public class CardServiceExtendedTest
{
    private SqliteDbFixture _db = null!;
    private CardService _service = null!;
    private SqliteDbFixture.Seed _seed = null!;
    private Actor _member = null!;

    [SetUp]
    public void Setup()
    {
        _db = new SqliteDbFixture();
        _seed = _db.SeedBoards();
        _service = new CardService(_db, new CardNumberingService(_db));
        _member = new Actor(_seed.Member.Id);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<Card> Create(string name, Column? column = null) =>
        (await _service.CreateAsync(_member, (column ?? _seed.Todo).Id, name)).Value!;

    private Label AddLabel(Board board, string name)
    {
        using var db = _db.CreateDbContext();
        var label = new Label { Name = name, BoardId = board.Id, TextColor = Color.White, BackgroundColor = Color.Black };
        db.Labels.Add(label);
        db.SaveChanges();
        return label;
    }

    private List<ActivityType> ActivityTypes(int cardId)
    {
        using var db = _db.CreateDbContext();
        return db.Activities.Where(x => x.CardId == cardId).OrderBy(x => x.Id).Select(x => x.ActivityType).ToList();
    }

    [Test]
    public async Task Update_Priority_LogsOnlyRealChanges()
    {
        var card = await Create("A");

        await _service.UpdateAsync(_member, card.Id, priority: CardPriority.Normal);
        await _service.UpdateAsync(_member, card.Id, priority: CardPriority.High);
        var dto = await _service.GetAsync(_member, card.Id);

        using var db = _db.CreateDbContext();
        var activity = db.Activities.Single();

        Assert.Multiple(() =>
        {
            Assert.That(dto!.Priority, Is.EqualTo(CardPriority.High));
            Assert.That(activity.ActivityType, Is.EqualTo(ActivityType.PriorityChanged));
            Assert.That(activity.Text, Is.EqualTo("<b>changed</b> the priority to <b>High</b>"));
        });
    }

    [Test]
    public async Task Delete_RemovesCardWithItsDataAndKeepsIndicesContiguous()
    {
        var a = await Create("A");
        var b = await Create("B");
        var c = await Create("C");
        await _service.AddCommentAsync(_member, b.Id, "bye");
        await _service.AddLinkAsync(_member, a.Id, b.Id, "blocks");

        var result = await _service.DeleteAsync(_member, b.Id);

        using var db = _db.CreateDbContext();

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.EqualTo(_seed.Board.Id));
            Assert.That(db.Cards.Where(x => x.ColumnId == _seed.Todo.Id).OrderBy(x => x.Index).Select(x => new { x.Name, x.Index }).ToList(),
                Is.EqualTo(new[] { new { Name = "A", Index = 0 }, new { Name = "C", Index = 1 } }));
            Assert.That(db.Comments.Any(x => x.CardId == b.Id), Is.False);
            Assert.That(db.Activities.Any(x => x.CardId == b.Id), Is.False);
            Assert.That(db.CardLinks.Any(), Is.False);
        });
    }

    [Test]
    public async Task Delete_RequiresAccess()
    {
        var card = await Create("A");

        var outsider = await _service.DeleteAsync(new Actor(_seed.Outsider.Id), card.Id);
        var scoped = await _service.DeleteAsync(new Actor(_seed.Member.Id, _seed.OtherBoard.Id), card.Id);

        using var db = _db.CreateDbContext();

        Assert.Multiple(() =>
        {
            Assert.That(outsider.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(scoped.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(db.Cards.Any(x => x.Id == card.Id), Is.True);
        });
    }

    [Test]
    public async Task Labels_AddAndRemoveAreIdempotentAndBoardBound()
    {
        var card = await Create("A");
        var bug = AddLabel(_seed.Board, "bug");
        var foreign = AddLabel(_seed.OtherBoard, "elsewhere");

        await _service.AddLabelAsync(_member, card.Id, bug.Id);
        await _service.AddLabelAsync(_member, card.Id, bug.Id);
        var labelled = await _service.GetAsync(_member, card.Id);
        await _service.RemoveLabelAsync(_member, card.Id, bug.Id);
        await _service.RemoveLabelAsync(_member, card.Id, bug.Id);
        var cleared = await _service.GetAsync(_member, card.Id);
        var wrongBoard = await _service.AddLabelAsync(_member, card.Id, foreign.Id);

        Assert.Multiple(() =>
        {
            Assert.That(labelled!.Labels, Is.EqualTo(new[] { "bug" }));
            Assert.That(cleared!.Labels, Is.Empty);
            Assert.That(ActivityTypes(card.Id), Is.EqualTo(new[] { ActivityType.LabelAdded, ActivityType.LabelRemoved }));
            Assert.That(wrongBoard.Error, Is.EqualTo(ServiceError.NotFound));
        });
    }

    [Test]
    public async Task AddLink_CreatesOppositePairWithActivityOnEachCard()
    {
        var a = await Create("A");
        var b = await Create("B");

        var result = await _service.AddLinkAsync(_member, a.Id, b.Id, "blocks");

        using var db = _db.CreateDbContext();
        var links = db.CardLinks.OrderBy(x => x.Id).ToList();
        var activities = db.Activities.Where(x => x.ActivityType == ActivityType.LinkAdded).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(links.Select(x => (x.CardOneId, x.CardTwoId, x.Category)),
                Is.EqualTo(new[] { (a.Id, b.Id, "blocks"), (b.Id, a.Id, "is blocked by") }));
            Assert.That(activities.Single(x => x.CardId == a.Id).Text, Does.Contain("TST-2</b> with <b>blocks"));
            Assert.That(activities.Single(x => x.CardId == b.Id).Text, Does.Contain("TST-1</b> with <b>is blocked by"));
        });
    }

    [Test]
    public async Task AddLink_RejectsSelfDuplicateUnknownCategoryAndInaccessibleTarget()
    {
        var a = await Create("A");
        var b = await Create("B");
        var other = await Create("Other", _seed.OtherColumn);
        await _service.AddLinkAsync(_member, a.Id, b.Id, "relates to");

        var self = await _service.AddLinkAsync(_member, a.Id, a.Id, "relates to");
        var duplicate = await _service.AddLinkAsync(_member, a.Id, b.Id, "blocks");
        var unknown = await _service.AddLinkAsync(_member, a.Id, b.Id, "loves");
        var scoped = await _service.AddLinkAsync(new Actor(_seed.Member.Id, _seed.Board.Id), a.Id, other.Id, "relates to");
        var crossBoard = await _service.AddLinkAsync(_member, a.Id, other.Id, "relates to");

        Assert.Multiple(() =>
        {
            Assert.That(self.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(duplicate.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(unknown.Error, Is.EqualTo(ServiceError.Invalid));
            Assert.That(unknown.Message, Does.Contain("is blocked by"));
            Assert.That(scoped.Error, Is.EqualTo(ServiceError.NotFound));
            Assert.That(crossBoard.Succeeded, Is.True, "linking across boards the user can access is allowed, as in the UI");
        });
    }

    [Test]
    public async Task RemoveLink_RemovesOnlyThatPairWhenCardHasSeveralLinks()
    {
        var a = await Create("A");
        var b = await Create("B");
        var c = await Create("C");
        await _service.AddLinkAsync(_member, a.Id, b.Id, "blocks");
        await _service.AddLinkAsync(_member, c.Id, a.Id, "relates to");

        var result = await _service.RemoveLinkAsync(_member, a.Id, b.Id);
        var missing = await _service.RemoveLinkAsync(_member, a.Id, b.Id);

        using var db = _db.CreateDbContext();
        var remaining = db.CardLinks.Select(x => new { x.CardOneId, x.CardTwoId }).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.EqualTo(2));
            Assert.That(remaining, Is.EquivalentTo(new[] { new { CardOneId = c.Id, CardTwoId = a.Id }, new { CardOneId = a.Id, CardTwoId = c.Id } }));
            Assert.That(db.Activities.Count(x => x.ActivityType == ActivityType.LinkRemoved), Is.EqualTo(2));
            Assert.That(db.Activities.Where(x => x.ActivityType == ActivityType.LinkRemoved).Select(x => x.CardId), Is.EquivalentTo(new[] { a.Id, b.Id }));
            Assert.That(missing.Error, Is.EqualTo(ServiceError.NotFound));
        });
    }

    [Test]
    public async Task Links_AreListedOnCardButHideTargetsOutsideTokenScope()
    {
        var a = await Create("A");
        var b = await Create("B");
        var other = await Create("Other", _seed.OtherColumn);
        await _service.AddLinkAsync(_member, a.Id, b.Id, "blocks");
        await _service.AddLinkAsync(_member, a.Id, other.Id, "relates to");

        var all = await _service.GetLinksAsync(_member, a.Id);
        var scoped = await _service.GetLinksAsync(new Actor(_seed.Member.Id, _seed.Board.Id), a.Id);
        var dto = await _service.GetAsync(new Actor(_seed.Member.Id, _seed.Board.Id), a.Id);

        Assert.Multiple(() =>
        {
            Assert.That(all!.Select(x => x.CardKey), Is.EqualTo(new[] { "TST-2", "OTH-1" }));
            Assert.That(scoped!.Select(x => x.CardKey), Is.EqualTo(new[] { "TST-2" }));
            Assert.That(dto!.Links.Select(x => (x.CardKey, x.Category)), Is.EqualTo(new[] { ("TST-2", "blocks") }));
        });
    }

    [Test]
    public async Task ResolveAsync_AcceptsIdOrKey()
    {
        var card = await Create("A");

        Assert.Multiple(async () =>
        {
            Assert.That((await _service.ResolveAsync(_member, card.Id.ToString()))!.Id, Is.EqualTo(card.Id));
            Assert.That((await _service.ResolveAsync(_member, " tst-1 "))!.Id, Is.EqualTo(card.Id));
            Assert.That(await _service.ResolveAsync(_member, "TST-404"), Is.Null);
        });
    }
}
