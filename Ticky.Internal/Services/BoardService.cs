namespace Ticky.Internal.Services;

/// <summary>
/// Read-side board queries shared by the REST API and the MCP endpoint.
/// </summary>
public class BoardService
{
    public const int MAX_ACTIVITY_LIMIT = 200;

    private readonly IDbContextFactory<DataContext> _dbContextFactory;

    public BoardService(IDbContextFactory<DataContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<List<BoardSummaryDto>> ListAsync(Actor actor)
    {
        using var db = _dbContextFactory.CreateDbContext();

        return await db.AccessibleBoards(actor)
            .OrderBy(x => x.Project.Name)
            .ThenBy(x => x.Name)
            .Select(x => new BoardSummaryDto(x.Id, x.Code, x.Name, x.Project.Name))
            .ToListAsync();
    }

    public async Task<BoardDto?> GetAsync(Actor actor, int boardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var board = await db.AccessibleBoards(actor)
            .Where(x => x.Id == boardId)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Name,
                x.Description,
                Columns = x.Columns
                    .OrderBy(c => c.Index)
                    .Select(c => new ColumnDto(c.Id, c.Name, c.Index, c.Finished, c.MaxCards, c.Cards.Count))
                    .ToList(),
                Labels = x.Labels.OrderBy(l => l.Name).Select(l => new LabelDto(l.Id, l.Name)).ToList(),
            })
            .FirstOrDefaultAsync();

        if (board is null)
            return null;

        var cards = await db.Cards
            .Where(x => x.Column.BoardId == boardId)
            .OrderBy(x => x.Column.Index)
            .ThenBy(x => x.Index)
            .Select(x => new CardSummaryDto(
                x.Id,
                board.Code + "-" + x.Number,
                x.Name,
                x.ColumnId,
                x.Column.Name,
                x.Index,
                x.Priority,
                x.Deadline,
                x.Flagged,
                x.Assignees.Select(a => a.DisplayName).ToList(),
                x.Labels.Select(l => l.Name).ToList()
            ))
            .ToListAsync();

        return new BoardDto(board.Id, board.Code, board.Name, board.Description, board.Columns, board.Labels, cards);
    }

    /// <summary>
    /// Labels defined on the board. Null if the board is not accessible.
    /// </summary>
    public async Task<List<LabelDto>?> GetLabelsAsync(Actor actor, int boardId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        if (!await db.AccessibleBoards(actor).AnyAsync(x => x.Id == boardId))
            return null;

        return await db.Labels
            .Where(x => x.BoardId == boardId)
            .OrderBy(x => x.Name)
            .Select(x => new LabelDto(x.Id, x.Name))
            .ToListAsync();
    }

    /// <summary>
    /// Adds a column at the end of the board. Like the UI, only board admins may do this.
    /// </summary>
    public async Task<ServiceResult<Column>> CreateColumnAsync(
        Actor actor,
        int boardId,
        string name,
        int maxCards = 0,
        bool finished = false,
        CardPlacement newCardPlacement = CardPlacement.Bottom
    )
    {
        name = name.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<Column>.Fail(ServiceError.Invalid, "Column name must not be empty.");

        if (maxCards < 0)
            return ServiceResult<Column>.Fail(ServiceError.Invalid, "Max cards must be 0 (unlimited) or more.");

        using var db = _dbContextFactory.CreateDbContext();

        if (!await db.AccessibleBoards(actor).AnyAsync(x => x.Id == boardId))
            return ServiceResult<Column>.Fail(ServiceError.NotFound, $"Board {boardId} not found.");

        if (!await db.AdministeredBoards(actor).AnyAsync(x => x.Id == boardId))
            return ServiceResult<Column>.Fail(ServiceError.Forbidden, "Only board admins can add columns.");

        var columns = await db.Columns.Where(x => x.BoardId == boardId).ToListAsync();

        var column = new Column
        {
            Name = name,
            BoardId = boardId,
            Index = columns.GetNextIndex(),
            MaxCards = maxCards,
            Finished = finished,
            NewCardPlacement = newCardPlacement,
        };

        db.Columns.Add(column);
        await db.SaveChangesAsync();

        return ServiceResult<Column>.Ok(column);
    }

    /// <summary>
    /// Most recent activity on the board, newest first. Returns null if the board is not accessible.
    /// </summary>
    public async Task<List<ActivityDto>?> GetActivityAsync(
        Actor actor,
        int boardId,
        ActivityType? type = null,
        int limit = 50,
        DateTime? before = null
    )
    {
        using var db = _dbContextFactory.CreateDbContext();

        if (!await db.AccessibleBoards(actor).AnyAsync(x => x.Id == boardId))
            return null;

        var query = db.Activities.Where(x => x.Card.Column.BoardId == boardId);

        if (type is not null)
            query = query.Where(x => x.ActivityType == type);

        if (before is not null)
            query = query.Where(x => x.CreatedAt < before);

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, MAX_ACTIVITY_LIMIT))
            .Select(x => new ActivityDto(
                x.Id,
                x.CardId,
                x.Card.Column.Board.Code + "-" + x.Card.Number,
                x.Card.Name,
                x.ActivityType,
                x.Text,
                x.User.DisplayName,
                x.CreatedAt,
                x.FromColumnName,
                x.ToColumnName,
                x.ToColumnFinished
            ))
            .ToListAsync();
    }

    /// <summary>
    /// Card counts per column plus the burndown series. "Done" counts cards currently in a Finished column;
    /// the burndown uses the Finished snapshot recorded on each move.
    /// </summary>
    public async Task<BoardStatsDto?> GetStatsAsync(Actor actor, int boardId, DateTime? today = null)
    {
        using var db = _dbContextFactory.CreateDbContext();

        if (!await db.AccessibleBoards(actor).AnyAsync(x => x.Id == boardId))
            return null;

        var columns = await db.Columns
            .Where(x => x.BoardId == boardId)
            .OrderBy(x => x.Index)
            .Select(x => new ColumnStatsDto(x.Id, x.Name, x.Finished, x.Cards.Count))
            .ToListAsync();

        var cardCreatedAts = await db.Cards
            .Where(x => x.Column.BoardId == boardId)
            .Select(x => x.CreatedAt)
            .ToListAsync();

        var completionDates = await db.Activities
            .Where(x =>
                x.ActivityType == ActivityType.CardMoved
                && x.ToColumnFinished == true
                && x.Card.Column.BoardId == boardId
            )
            .GroupBy(x => x.CardId)
            .Select(g => g.Min(x => x.CreatedAt))
            .ToListAsync();

        var burndown = BurndownHelper
            .GetOpenPerDay(cardCreatedAts, completionDates, today ?? DateTime.Today)
            .Select(x => new BurndownPointDto(DateOnly.FromDateTime(x.Date), x.Open))
            .ToList();

        int total = columns.Sum(x => x.Cards);
        int done = columns.Where(x => x.Finished).Sum(x => x.Cards);

        return new BoardStatsDto(boardId, total, done, total - done, columns, burndown);
    }
}
