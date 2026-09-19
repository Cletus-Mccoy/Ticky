using Ticky.Base.DTOs.Api;

namespace Ticky.Web.Controllers;

[Authorize(Policy = ApiTokenAuthenticationHandler.POLICY)]
[Route("api/boards")]
[ApiController]
public class BoardsController : ControllerBase
{
    private readonly BoardService _boardService;

    public BoardsController(BoardService boardService)
    {
        _boardService = boardService;
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

    [HttpGet("{id:int}/stats")]
    public async Task<ActionResult<BoardStatsDto>> Stats(int id) =>
        await _boardService.GetStatsAsync(User.ToActor(), id) is { } stats ? stats : NotFound();
}
