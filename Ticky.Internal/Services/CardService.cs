namespace Ticky.Internal.Services;

/// <summary>
/// Card operations shared by the UI, the REST API and the MCP endpoint, so every entry point
/// applies the same rules and records the same activity.
/// </summary>
public class CardService
{
    private readonly IDbContextFactory<DataContext> _dbContextFactory;
    private readonly CardNumberingService _cardNumberingService;

    public CardService(IDbContextFactory<DataContext> dbContextFactory, CardNumberingService cardNumberingService)
    {
        _dbContextFactory = dbContextFactory;
        _cardNumberingService = cardNumberingService;
    }

    public async Task<ServiceResult<Card>> CreateAsync(Actor actor, int columnId, string name, string? description = null)
    {
        name = name.ReplaceLineEndings(" ").Trim();

        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<Card>.Fail(ServiceError.Invalid, "Card name must not be empty.");

        using var db = _dbContextFactory.CreateDbContext();

        var column = await db.Columns
            .Include(x => x.Cards)
            .Where(x => db.AccessibleBoards(actor).Any(b => b.Id == x.BoardId))
            .FirstOrDefaultAsync(x => x.Id == columnId);

        if (column is null)
            return ServiceResult<Card>.Fail(ServiceError.NotFound, $"Column {columnId} not found.");

        if (column.MaxCards != 0 && column.Cards.Count >= column.MaxCards)
            return ServiceResult<Card>.Fail(ServiceError.ColumnFull, $"Column '{column.Name}' is full.");

        int newIndex = 0;

        if (column.NewCardPlacement == CardPlacement.Top)
            column.Cards.ForEach(x => x.Index++);
        else
            newIndex = column.Cards.GetNextIndex();

        var card = new Card
        {
            Name = name,
            Description = description ?? string.Empty,
            ColumnId = column.Id,
            Index = newIndex,
            Number = await _cardNumberingService.GetNextNumberAsync(column.BoardId),
            CreatedById = actor.UserId,
        };

        var user = await db.Users.FirstAsync(x => x.Id == actor.UserId);

        if (user.AutomaticAssign)
            card.Assignees.Add(user);

        column.Cards.Add(card);
        await db.SaveChangesAsync();

        return ServiceResult<Card>.Ok(card);
    }

    /// <summary>
    /// Moves a card to another column on the same board. The card is placed before
    /// <paramref name="beforeCardId"/> if given, otherwise at <paramref name="index"/>, otherwise at the bottom.
    /// </summary>
    public async Task<ServiceResult<Card>> MoveAsync(
        Actor actor,
        int cardId,
        int targetColumnId,
        int? index = null,
        int? beforeCardId = null
    )
    {
        using var db = _dbContextFactory.CreateDbContext();

        var card = await db.AccessibleCards(actor)
            .Include(x => x.Column)
            .FirstOrDefaultAsync(x => x.Id == cardId);

        if (card is null)
            return ServiceResult<Card>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        if (card.ColumnId == targetColumnId)
            return ServiceResult<Card>.Fail(ServiceError.Invalid, $"Card is already in '{card.Column.Name}'.");

        var columns = await db.Columns
            .Include(x => x.Cards)
            .Where(x => x.Id == card.ColumnId || x.Id == targetColumnId)
            .ToListAsync();

        var fromColumn = columns.First(x => x.Id == card.ColumnId);
        var toColumn = columns.FirstOrDefault(x => x.Id == targetColumnId);

        if (toColumn is null || toColumn.BoardId != fromColumn.BoardId)
            return ServiceResult<Card>.Fail(ServiceError.NotFound, $"Column {targetColumnId} not found on this board.");

        if (toColumn.MaxCards != 0 && toColumn.Cards.Count >= toColumn.MaxCards)
            return ServiceResult<Card>.Fail(ServiceError.ColumnFull, $"Column '{toColumn.Name}' is full.");

        var movedCard = fromColumn.Cards.First(x => x.Id == cardId);
        fromColumn.Cards.Remove(movedCard);
        fromColumn.Cards.FixIndices();

        int targetIndex = toColumn.Cards.GetNextIndex();

        if (beforeCardId is not null && toColumn.Cards.FirstOrDefault(x => x.Id == beforeCardId) is { } beforeCard)
            targetIndex = beforeCard.Index;
        else if (index is not null)
            targetIndex = Math.Clamp(index.Value, 0, toColumn.Cards.Count);

        foreach (var other in toColumn.Cards.Where(x => x.Index >= targetIndex))
            other.Index++;

        movedCard.Index = targetIndex;
        movedCard.ColumnId = toColumn.Id;
        toColumn.Cards.Add(movedCard);
        toColumn.Cards.FixIndices();

        db.Activities.Add(Activity.CardMoved(movedCard.Id, actor.UserId, fromColumn, toColumn));

        await db.SaveChangesAsync();

        movedCard.Column = toColumn;
        return ServiceResult<Card>.Ok(movedCard);
    }

    public async Task<ServiceResult<Card>> UpdateAsync(Actor actor, int cardId, string? name = null, string? description = null)
    {
        if (name is not null)
        {
            name = name.ReplaceLineEndings(" ").Trim();

            if (string.IsNullOrWhiteSpace(name))
                return ServiceResult<Card>.Fail(ServiceError.Invalid, "Card name must not be empty.");
        }

        using var db = _dbContextFactory.CreateDbContext();

        var card = await db.AccessibleCards(actor).FirstOrDefaultAsync(x => x.Id == cardId);

        if (card is null)
            return ServiceResult<Card>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        if (name is not null && name != card.Name)
        {
            card.Name = name;
            db.Activities.Add(new Activity
            {
                ActivityType = ActivityType.TitleChanged,
                Text = "<b>changed</b> the title",
                UserId = actor.UserId,
                CardId = card.Id,
            });
        }

        if (description is not null && description != card.Description)
        {
            card.Description = description;
            db.Activities.Add(new Activity
            {
                ActivityType = ActivityType.DescriptionChanged,
                Text = "<b>changed</b> the description",
                UserId = actor.UserId,
                CardId = card.Id,
            });
        }

        await db.SaveChangesAsync();

        return ServiceResult<Card>.Ok(card);
    }

    public async Task<ServiceResult<Comment>> AddCommentAsync(Actor actor, int cardId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ServiceResult<Comment>.Fail(ServiceError.Invalid, "Comment must not be empty.");

        using var db = _dbContextFactory.CreateDbContext();

        var card = await db.AccessibleCards(actor).FirstOrDefaultAsync(x => x.Id == cardId);

        if (card is null)
            return ServiceResult<Comment>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        var comment = new Comment
        {
            Text = text,
            CreatedById = actor.UserId,
            CardId = card.Id,
        };

        db.Comments.Add(comment);
        db.Activities.Add(new Activity
        {
            ActivityType = ActivityType.CommentPosted,
            Text = "<b>posted</b> a comment",
            UserId = actor.UserId,
            CardId = card.Id,
        });

        await db.SaveChangesAsync();

        return ServiceResult<Comment>.Ok(comment);
    }

    public async Task<CardDto?> GetAsync(Actor actor, int cardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        return await db.AccessibleCards(actor)
            .Where(x => x.Id == cardId)
            .Select(ToDto)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Looks a card up by its human key, e.g. "TCK-42".
    /// </summary>
    public async Task<CardDto?> GetByKeyAsync(Actor actor, string key)
    {
        var separator = key.LastIndexOf('-');

        if (separator <= 0 || !int.TryParse(key[(separator + 1)..], out var number))
            return null;

        var code = key[..separator].ToUpperInvariant();

        using var db = _dbContextFactory.CreateDbContext();

        return await db.AccessibleCards(actor)
            .Where(x => x.Number == number && x.Column.Board.Code == code)
            .Select(ToDto)
            .FirstOrDefaultAsync();
    }

    private static readonly System.Linq.Expressions.Expression<Func<Card, CardDto>> ToDto = x => new CardDto(
        x.Id,
        x.Column.Board.Code + "-" + x.Number,
        x.Column.BoardId,
        x.Name,
        x.Description,
        x.ColumnId,
        x.Column.Name,
        x.Column.Finished,
        x.Index,
        x.Priority,
        x.Deadline,
        x.Flagged,
        x.CreatedBy.DisplayName,
        x.CreatedAt,
        x.Assignees.Select(a => a.DisplayName).ToList(),
        x.Labels.Select(l => l.Name).ToList(),
        x.Comments.OrderBy(c => c.CreatedAt).Select(c => new CommentDto(c.Id, c.CreatedBy.DisplayName, c.Text, c.CreatedAt)).ToList()
    );
}
