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

    public async Task<ServiceResult<Card>> UpdateAsync(
        Actor actor,
        int cardId,
        string? name = null,
        string? description = null,
        CardPriority? priority = null
    )
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

        if (priority is not null && priority != card.Priority)
        {
            card.Priority = priority.Value;
            db.Activities.Add(new Activity
            {
                ActivityType = ActivityType.PriorityChanged,
                Text = $"<b>changed</b> the priority to <b>{priority}</b>",
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

    /// <summary>
    /// Deletes a card; comments, activity, links and labels go with it. Returns the id of the card's board.
    /// </summary>
    public async Task<ServiceResult<int>> DeleteAsync(Actor actor, int cardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var card = await db.AccessibleCards(actor)
            .Include(x => x.Column)
            .FirstOrDefaultAsync(x => x.Id == cardId);

        if (card is null)
            return ServiceResult<int>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        await db.Cards.Where(x => x.Id == cardId).ExecuteDeleteAsync();

        var remaining = await db.Cards.Where(x => x.ColumnId == card.ColumnId).ToListAsync();
        remaining.FixIndices();
        await db.SaveChangesAsync();

        return ServiceResult<int>.Ok(card.Column.BoardId);
    }

    public Task<ServiceResult<Card>> AddLabelAsync(Actor actor, int cardId, int labelId) =>
        SetLabelAsync(actor, cardId, labelId, add: true);

    public Task<ServiceResult<Card>> RemoveLabelAsync(Actor actor, int cardId, int labelId) =>
        SetLabelAsync(actor, cardId, labelId, add: false);

    /// <summary>
    /// Adds or removes a label from the card's board. Idempotent: no activity is recorded if nothing changes.
    /// </summary>
    private async Task<ServiceResult<Card>> SetLabelAsync(Actor actor, int cardId, int labelId, bool add)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var card = await db.AccessibleCards(actor)
            .Include(x => x.Column)
            .Include(x => x.Labels)
            .FirstOrDefaultAsync(x => x.Id == cardId);

        if (card is null)
            return ServiceResult<Card>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        var label = await db.Labels.FirstOrDefaultAsync(x => x.Id == labelId && x.BoardId == card.Column.BoardId);

        if (label is null)
            return ServiceResult<Card>.Fail(ServiceError.NotFound, $"Label {labelId} not found on this card's board.");

        var hasLabel = card.Labels.Any(x => x.Id == labelId);

        if (add && !hasLabel)
        {
            card.Labels.Add(label);
            db.Activities.Add(new Activity
            {
                ActivityType = ActivityType.LabelAdded,
                Text = $"<b>added</b> label <b>{label.Name}</b>",
                UserId = actor.UserId,
                CardId = card.Id,
            });
        }
        else if (!add && hasLabel)
        {
            card.Labels.RemoveAll(x => x.Id == labelId);
            db.Activities.Add(new Activity
            {
                ActivityType = ActivityType.LabelRemoved,
                Text = $"<b>removed</b> label <b>{label.Name}</b>",
                UserId = actor.UserId,
                CardId = card.Id,
            });
        }

        await db.SaveChangesAsync();

        return ServiceResult<Card>.Ok(card);
    }

    public static IEnumerable<string> LinkCategories =>
        Constants.LINK_TYPE_PAIRS.Keys.Concat(Constants.LINK_TYPE_PAIRS.Values).Distinct();

    private static string OppositeCategory(string category) =>
        Constants.LINK_TYPE_PAIRS.TryGetValue(category, out var opposite)
            ? opposite
            : Constants.LINK_TYPE_PAIRS.First(x => x.Value == category).Key;

    /// <summary>
    /// Links two cards. Like the UI, this stores a link on each card with opposite categories
    /// (e.g. "blocks" / "is blocked by") and records the activity on both.
    /// </summary>
    public async Task<ServiceResult<CardLink>> AddLinkAsync(Actor actor, int cardId, int targetCardId, string category)
    {
        category = category.Trim();

        if (!LinkCategories.Contains(category))
        {
            return ServiceResult<CardLink>.Fail(
                ServiceError.Invalid,
                $"Unknown link category '{category}'. Use one of: {string.Join(", ", LinkCategories)}."
            );
        }

        if (cardId == targetCardId)
            return ServiceResult<CardLink>.Fail(ServiceError.Invalid, "A card cannot be linked to itself.");

        using var db = _dbContextFactory.CreateDbContext();

        var cards = await db.AccessibleCards(actor)
            .Include(x => x.Column)
                .ThenInclude(x => x.Board)
            .Where(x => x.Id == cardId || x.Id == targetCardId)
            .ToListAsync();

        var card = cards.FirstOrDefault(x => x.Id == cardId);
        var target = cards.FirstOrDefault(x => x.Id == targetCardId);

        if (card is null)
            return ServiceResult<CardLink>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        if (target is null)
            return ServiceResult<CardLink>.Fail(ServiceError.NotFound, $"Target card {targetCardId} not found.");

        if (await db.CardLinks.AnyAsync(x => x.CardOneId == cardId && x.CardTwoId == targetCardId))
            return ServiceResult<CardLink>.Fail(ServiceError.Invalid, "This card is already linked to the target card.");

        var oppositeCategory = OppositeCategory(category);

        var link = new CardLink { CardOneId = card.Id, CardTwoId = target.Id, Category = category };
        db.CardLinks.Add(link);
        db.CardLinks.Add(new CardLink { CardOneId = target.Id, CardTwoId = card.Id, Category = oppositeCategory });

        db.Activities.Add(new Activity
        {
            ActivityType = ActivityType.LinkAdded,
            Text = $"<b>added</b> a linked issue <b>{target.Column.Board.Code}-{target.Number}</b> with <b>{category}</b> relationship",
            UserId = actor.UserId,
            CardId = card.Id,
        });
        db.Activities.Add(new Activity
        {
            ActivityType = ActivityType.LinkAdded,
            Text = $"<b>added</b> a linked issue <b>{card.Column.Board.Code}-{card.Number}</b> with <b>{oppositeCategory}</b> relationship",
            UserId = actor.UserId,
            CardId = target.Id,
        });

        await db.SaveChangesAsync();

        return ServiceResult<CardLink>.Ok(link);
    }

    /// <summary>
    /// Removes the link between two cards in both directions. Returns the number of link rows removed.
    /// </summary>
    public async Task<ServiceResult<int>> RemoveLinkAsync(Actor actor, int cardId, int targetCardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        if (!await db.AccessibleCards(actor).AnyAsync(x => x.Id == cardId))
            return ServiceResult<int>.Fail(ServiceError.NotFound, $"Card {cardId} not found.");

        var links = await db.CardLinks
            .Include(x => x.CardTwo)
                .ThenInclude(x => x.Column)
                    .ThenInclude(x => x.Board)
            .Where(x =>
                (x.CardOneId == cardId && x.CardTwoId == targetCardId)
                || (x.CardOneId == targetCardId && x.CardTwoId == cardId)
            )
            .ToListAsync();

        if (!links.Any(x => x.CardOneId == cardId))
            return ServiceResult<int>.Fail(ServiceError.NotFound, $"Card {cardId} is not linked to card {targetCardId}.");

        foreach (var link in links)
        {
            db.CardLinks.Remove(link);
            db.Activities.Add(new Activity
            {
                ActivityType = ActivityType.LinkRemoved,
                Text = $"<b>deleted</b> a linked issue <b>{link.CardTwo.Column.Board.Code}-{link.CardTwo.Number}</b> with <b>{link.Category}</b> relationship",
                UserId = actor.UserId,
                CardId = link.CardOneId,
            });
        }

        await db.SaveChangesAsync();

        return ServiceResult<int>.Ok(links.Count);
    }

    /// <summary>
    /// Links from this card, limited to target cards the actor can access. Null if the card is not accessible.
    /// </summary>
    public async Task<List<CardLinkDto>?> GetLinksAsync(Actor actor, int cardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        if (!await db.AccessibleCards(actor).AnyAsync(x => x.Id == cardId))
            return null;

        return await db.CardLinks
            .Where(x => x.CardOneId == cardId && db.AccessibleCards(actor).Any(c => c.Id == x.CardTwoId))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new CardLinkDto(
                x.Id,
                x.Category,
                x.CardTwoId,
                x.CardTwo.Column.Board.Code + "-" + x.CardTwo.Number,
                x.CardTwo.Name,
                x.CardTwo.Column.Name
            ))
            .ToListAsync();
    }

    /// <summary>
    /// Resolves "42" (card id) or "TCK-42" (card key) to an accessible card.
    /// </summary>
    public async Task<CardDto?> ResolveAsync(Actor actor, string reference) =>
        int.TryParse(reference.Trim(), out var id)
            ? await GetAsync(actor, id)
            : await GetByKeyAsync(actor, reference.Trim());

    public async Task<CardDto?> GetAsync(Actor actor, int cardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        return await db.AccessibleCards(actor)
            .Where(x => x.Id == cardId)
            .Select(ToDto(db, actor))
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
            .Select(ToDto(db, actor))
            .FirstOrDefaultAsync();
    }

    private static System.Linq.Expressions.Expression<Func<Card, CardDto>> ToDto(DataContext db, Actor actor) =>
        x => new CardDto(
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
            x.LinkedIssuesOne
                .Where(l => db.AccessibleCards(actor).Any(c => c.Id == l.CardTwoId))
                .OrderBy(l => l.CreatedAt)
                .Select(l => new CardLinkDto(
                    l.Id,
                    l.Category,
                    l.CardTwoId,
                    l.CardTwo.Column.Board.Code + "-" + l.CardTwo.Number,
                    l.CardTwo.Name,
                    l.CardTwo.Column.Name
                ))
                .ToList(),
            x.Comments.OrderBy(c => c.CreatedAt).Select(c => new CommentDto(c.Id, c.CreatedBy.DisplayName, c.Text, c.CreatedAt)).ToList()
        );
}
