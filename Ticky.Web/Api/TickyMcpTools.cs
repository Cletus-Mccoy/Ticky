using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Ticky.Base.DTOs.Api;

namespace Ticky.Web.Api;

/// <summary>
/// MCP tools exposed at /mcp. They act as the user owning the API token and go through the same services as the UI,
/// so changes show up in card timelines and refresh open boards.
/// </summary>
[McpServerToolType]
public class TickyMcpTools
{
    private readonly CardService _cardService;
    private readonly BoardService _boardService;
    private readonly BoardNotifier _boardNotifier;
    private readonly Actor _actor;

    public TickyMcpTools(
        CardService cardService,
        BoardService boardService,
        BoardNotifier boardNotifier,
        IHttpContextAccessor httpContextAccessor
    )
    {
        _cardService = cardService;
        _boardService = boardService;
        _boardNotifier = boardNotifier;
        _actor = httpContextAccessor.HttpContext!.User.ToActor();
    }

    [McpServerTool(Name = "list_boards", ReadOnly = true, Idempotent = true)]
    [Description("Lists the boards you can access, with their id and code. Card keys are <code>-<number>, e.g. TCK-42.")]
    public Task<List<BoardSummaryDto>> ListBoards() => _boardService.ListAsync(_actor);

    [McpServerTool(Name = "get_board", ReadOnly = true, Idempotent = true)]
    [Description("Gets a board's columns (in order, with which ones count as finished), its labels and all its cards.")]
    public async Task<BoardDto> GetBoard([Description("Board id or code, e.g. 12 or TCK.")] string board) =>
        await _boardService.GetAsync(_actor, await ResolveBoardIdAsync(board))
        ?? throw new McpException($"Board '{board}' not found.");

    [McpServerTool(Name = "get_card", ReadOnly = true, Idempotent = true)]
    [Description("Gets a card with its description, column, priority, assignees, labels, links and comments.")]
    public Task<CardDto> GetCard([Description("Card key like TCK-42, or numeric card id.")] string card) =>
        ResolveCardAsync(card);

    [McpServerTool(Name = "create_card")]
    [Description("Creates a card in a column. Placement follows the column's new-card setting.")]
    public async Task<CardDto> CreateCard(
        [Description("Board id or code.")] string board,
        [Description("Column name (case-insensitive) or id.")] string column,
        [Description("Card title.")] string name,
        [Description("Optional card description (markdown).")] string? description = null
    )
    {
        var boardDto = await GetBoard(board);
        var columnId = ResolveColumnId(boardDto, column);

        var result = await _cardService.CreateAsync(_actor, columnId, name, description);
        return await ChangedAsync(Unwrap(result).Id);
    }

    [McpServerTool(Name = "update_card", Idempotent = true)]
    [Description("Changes a card's title, description and/or priority. Omitted fields are left unchanged.")]
    public async Task<CardDto> UpdateCard(
        [Description("Card key like TCK-42, or numeric card id.")] string card,
        [Description("New title.")] string? name = null,
        [Description("New description (markdown). Replaces the existing description.")] string? description = null,
        [Description("New priority: Normal, Medium, High or Critical.")] CardPriority? priority = null
    )
    {
        var cardDto = await ResolveCardAsync(card);
        Unwrap(await _cardService.UpdateAsync(_actor, cardDto.Id, name, description, priority));
        return await ChangedAsync(cardDto.Id);
    }

    [McpServerTool(Name = "delete_card", Destructive = true)]
    [Description("Permanently deletes a card with its comments, history, links and labels. Cannot be undone.")]
    public async Task<string> DeleteCard([Description("Card key like TCK-42, or numeric card id.")] string card)
    {
        var cardDto = await ResolveCardAsync(card);
        var boardId = Unwrap(await _cardService.DeleteAsync(_actor, cardDto.Id));
        await _boardNotifier.BoardChangedAsync(boardId);
        return $"Deleted {cardDto.Key} ({cardDto.Name}).";
    }

    [McpServerTool(Name = "link_cards")]
    [Description(
        "Links two cards, like 'TCK-1 blocks TCK-2'. The opposite link is added to the other card automatically. "
            + "Categories: blocks, is blocked by, tests, is tested by, relates to, repeats, is repeated by."
    )]
    public async Task<CardDto> LinkCards(
        [Description("Card key or id the link starts from.")] string card,
        [Description("How this card relates to the target, e.g. 'blocks' or 'relates to'.")] string category,
        [Description("Target card key or id.")] string target
    )
    {
        var cardDto = await ResolveCardAsync(card);
        var targetDto = await ResolveCardAsync(target);
        Unwrap(await _cardService.AddLinkAsync(_actor, cardDto.Id, targetDto.Id, category));
        await _boardNotifier.BoardChangedAsync(targetDto.BoardId);
        return await ChangedAsync(cardDto.Id);
    }

    [McpServerTool(Name = "unlink_cards", Idempotent = true)]
    [Description("Removes the link between two cards, in both directions.")]
    public async Task<CardDto> UnlinkCards(
        [Description("Card key or id.")] string card,
        [Description("Linked card key or id.")] string target
    )
    {
        var cardDto = await ResolveCardAsync(card);
        var targetDto = await ResolveCardAsync(target);
        Unwrap(await _cardService.RemoveLinkAsync(_actor, cardDto.Id, targetDto.Id));
        await _boardNotifier.BoardChangedAsync(targetDto.BoardId);
        return await ChangedAsync(cardDto.Id);
    }

    [McpServerTool(Name = "list_labels", ReadOnly = true, Idempotent = true)]
    [Description("Lists the labels defined on a board.")]
    public async Task<List<LabelDto>> ListLabels([Description("Board id or code.")] string board) =>
        await _boardService.GetLabelsAsync(_actor, await ResolveBoardIdAsync(board))
        ?? throw new McpException($"Board '{board}' not found.");

    [McpServerTool(Name = "add_label", Idempotent = true)]
    [Description("Adds one of the board's labels to a card.")]
    public Task<CardDto> AddLabel(
        [Description("Card key or id.")] string card,
        [Description("Label name (case-insensitive) or id.")] string label
    ) => SetLabelAsync(card, label, add: true);

    [McpServerTool(Name = "remove_label", Idempotent = true)]
    [Description("Removes a label from a card.")]
    public Task<CardDto> RemoveLabel(
        [Description("Card key or id.")] string card,
        [Description("Label name (case-insensitive) or id.")] string label
    ) => SetLabelAsync(card, label, add: false);

    [McpServerTool(Name = "create_column")]
    [Description("Adds a column at the end of a board. Requires board admin rights.")]
    public async Task<ColumnDto> CreateColumn(
        [Description("Board id or code.")] string board,
        [Description("Column name.")] string name,
        [Description("Maximum number of cards, 0 for unlimited.")] int maxCards = 0,
        [Description("Whether cards in this column count as finished (done).")] bool finished = false,
        [Description("Where new cards go: Top or Bottom.")] CardPlacement newCardPlacement = CardPlacement.Bottom
    )
    {
        var boardId = await ResolveBoardIdAsync(board);
        var column = Unwrap(await _boardService.CreateColumnAsync(_actor, boardId, name, maxCards, finished, newCardPlacement));
        await _boardNotifier.BoardChangedAsync(boardId);
        return new ColumnDto(column.Id, column.Name, column.Index, column.Finished, column.MaxCards, 0);
    }

    [McpServerTool(Name = "move_card")]
    [Description("Moves a card to another column on its board and records it in the card's history.")]
    public async Task<CardDto> MoveCard(
        [Description("Card key like TCK-42, or numeric card id.")] string card,
        [Description("Target column name (case-insensitive) or id.")] string column,
        [Description("Optional 0-based position in the target column; 0 is the top. Defaults to the bottom.")] int? position = null
    )
    {
        var cardDto = await ResolveCardAsync(card);
        var boardDto = await GetBoard(cardDto.BoardId.ToString());
        var columnId = ResolveColumnId(boardDto, column);

        Unwrap(await _cardService.MoveAsync(_actor, cardDto.Id, columnId, position));
        return await ChangedAsync(cardDto.Id);
    }

    [McpServerTool(Name = "add_comment")]
    [Description("Adds a comment to a card.")]
    public async Task<CardDto> AddComment(
        [Description("Card key like TCK-42, or numeric card id.")] string card,
        [Description("Comment text (markdown).")] string text
    )
    {
        var cardDto = await ResolveCardAsync(card);
        Unwrap(await _cardService.AddCommentAsync(_actor, cardDto.Id, text));
        return await ChangedAsync(cardDto.Id);
    }

    [McpServerTool(Name = "board_activity", ReadOnly = true, Idempotent = true)]
    [Description("Recent activity on a board, newest first: moves (with from/to columns), comments, edits and more.")]
    public async Task<List<ActivityDto>> BoardActivity(
        [Description("Board id or code.")] string board,
        [Description("Optional activity type filter, e.g. CardMoved, CommentPosted, TitleChanged.")] ActivityType? type = null,
        [Description("Maximum number of entries (1-200). Defaults to 50.")] int limit = 50
    ) =>
        await _boardService.GetActivityAsync(_actor, await ResolveBoardIdAsync(board), type, limit)
        ?? throw new McpException($"Board '{board}' not found.");

    [McpServerTool(Name = "board_stats", ReadOnly = true, Idempotent = true)]
    [Description("Card counts per column, total/done/open, and the daily burndown series.")]
    public async Task<BoardStatsDto> BoardStats([Description("Board id or code.")] string board) =>
        await _boardService.GetStatsAsync(_actor, await ResolveBoardIdAsync(board))
        ?? throw new McpException($"Board '{board}' not found.");

    private async Task<CardDto> SetLabelAsync(string card, string label, bool add)
    {
        var cardDto = await ResolveCardAsync(card);
        var labels = await _boardService.GetLabelsAsync(_actor, cardDto.BoardId) ?? [];

        var match = int.TryParse(label, out var labelId)
            ? labels.FirstOrDefault(x => x.Id == labelId)
            : labels.FirstOrDefault(x => x.Name.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new McpException(
                $"Label '{label}' not found on this board. Labels: {(labels.Count == 0 ? "none" : string.Join(", ", labels.Select(x => x.Name)))}."
            );
        }

        Unwrap(add
            ? await _cardService.AddLabelAsync(_actor, cardDto.Id, match.Id)
            : await _cardService.RemoveLabelAsync(_actor, cardDto.Id, match.Id));

        return await ChangedAsync(cardDto.Id);
    }

    private async Task<int> ResolveBoardIdAsync(string board)
    {
        if (int.TryParse(board, out var id))
            return id;

        var match = (await _boardService.ListAsync(_actor))
            .FirstOrDefault(x => x.Code.Equals(board.Trim(), StringComparison.OrdinalIgnoreCase));

        return match?.Id ?? throw new McpException($"Board '{board}' not found. Use list_boards to see available boards.");
    }

    private async Task<CardDto> ResolveCardAsync(string card) =>
        await _cardService.ResolveAsync(_actor, card) ?? throw new McpException($"Card '{card}' not found.");

    private static int ResolveColumnId(BoardDto board, string column)
    {
        var match = int.TryParse(column, out var id)
            ? board.Columns.FirstOrDefault(x => x.Id == id)
            : board.Columns.FirstOrDefault(x => x.Name.Equals(column.Trim(), StringComparison.OrdinalIgnoreCase));

        return match?.Id
            ?? throw new McpException(
                $"Column '{column}' not found on {board.Code}. Columns: {string.Join(", ", board.Columns.Select(x => x.Name))}."
            );
    }

    private static T Unwrap<T>(ServiceResult<T> result) =>
        result.Succeeded ? result.Value! : throw new McpException(result.Message!);

    private async Task<CardDto> ChangedAsync(int cardId)
    {
        var card = (await _cardService.GetAsync(_actor, cardId))!;
        await _boardNotifier.BoardChangedAsync(card.BoardId);
        return card;
    }
}
