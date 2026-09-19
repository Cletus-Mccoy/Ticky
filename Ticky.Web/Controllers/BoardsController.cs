using Ticky.Base.DTOs.Api;

namespace Ticky.Web.Controllers;

[Authorize(Policy = ApiTokenAuthenticationHandler.POLICY)]
[Route("api/boards")]
[ApiController]
public class BoardsController : ControllerBase
{
    private readonly BoardService _boardService;
    private readonly BoardNotifier _boardNotifier;

    public BoardsController(BoardService boardService, BoardNotifier boardNotifier)
    {
        _boardService = boardService;
        _boardNotifier = boardNotifier;
    }

    [HttpGet]
    public async Task<List<BoardSummaryDto>> List() => await _boardService.ListAsync(User.ToActor());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BoardDto>> Get(int id) =>
        await _boardService.GetAsync(User.ToActor(), id) is { } board ? board : NotFound();

    [HttpGet("{id:int}/activity")]
    public async Task<ActionResult<List<ActivityDto>>> Activity(
        int id,
        [FromQuery] ActivityType? type,
        [FromQuery] int limit = 50,
        [FromQuery] DateTime? before = null
    ) =>
        await _boardService.GetActivityAsync(User.ToActor(), id, type, limit, before) is { } activity
            ? activity
            : NotFound();

    [HttpGet("{id:int}/labels")]
    public async Task<ActionResult<List<LabelDto>>> Labels(int id) =>
        await _boardService.GetLabelsAsync(User.ToActor(), id) is { } labels ? labels : NotFound();

    /// <summary>
    /// Adds a column at the end of the board. Board admins only.
    /// </summary>
    [HttpPost("{id:int}/columns")]
    public async Task<IActionResult> CreateColumn(int id, CreateColumnRequest request)
    {
        var result = await _boardService.CreateColumnAsync(
            User.ToActor(),
            id,
            request.Name!,
            request.MaxCards ?? 0,
            request.Finished ?? false,
            request.NewCardPlacement ?? CardPlacement.Bottom
        );

        if (!result.Succeeded)
            return this.ToProblem(result);

        await _boardNotifier.BoardChangedAsync(id);

        var column = result.Value!;
        return Created(
            $"/api/boards/{id}",
            new ColumnDto(column.Id, column.Name, column.Index, column.Finished, column.MaxCards, 0)
        );
    }

    [HttpGet("{id:int}/stats")]
    public async Task<ActionResult<BoardStatsDto>> Stats(int id) =>
        await _boardService.GetStatsAsync(User.ToActor(), id) is { } stats ? stats : NotFound();
}
