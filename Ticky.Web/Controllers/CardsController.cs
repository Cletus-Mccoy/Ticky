using Ticky.Base.DTOs.Api;

namespace Ticky.Web.Controllers;

[Authorize(Policy = ApiTokenAuthenticationHandler.POLICY)]
[Route("api/cards")]
[ApiController]
public class CardsController : ControllerBase
{
    private readonly CardService _cardService;
    private readonly BoardNotifier _boardNotifier;

    public CardsController(CardService cardService, BoardNotifier boardNotifier)
    {
        _cardService = cardService;
        _boardNotifier = boardNotifier;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CardDto>> Get(int id) =>
        await _cardService.GetAsync(User.ToActor(), id) is { } card ? card : NotFound();

    /// <summary>
    /// Looks a card up by its key, e.g. GET api/cards/TCK-42.
    /// </summary>
    [HttpGet(@"{key:regex(^[[A-Za-z0-9]]+-\d+$)}")]
    public async Task<ActionResult<CardDto>> GetByKey(string key) =>
        await _cardService.GetByKeyAsync(User.ToActor(), key) is { } card ? card : NotFound();

    [HttpPost]
    public async Task<IActionResult> Create(CreateCardRequest request)
    {
        var result = await _cardService.CreateAsync(User.ToActor(), request.ColumnId!.Value, request.Name!, request.Description);

        if (!result.Succeeded)
            return this.ToProblem(result);

        var card = await ChangedAsync(result.Value!.Id);
        return CreatedAtAction(nameof(Get), new { id = card.Id }, card);
    }

    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateCardRequest request)
    {
        var result = await _cardService.UpdateAsync(User.ToActor(), id, request.Name, request.Description, request.Priority);
        return result.Succeeded ? Ok(await ChangedAsync(id)) : this.ToProblem(result);
    }

    [HttpPost("{id:int}/move")]
    public async Task<IActionResult> Move(int id, MoveCardRequest request)
    {
        var result = await _cardService.MoveAsync(User.ToActor(), id, request.ColumnId!.Value, request.Index, request.BeforeCardId);
        return result.Succeeded ? Ok(await ChangedAsync(id)) : this.ToProblem(result);
    }

    [HttpPost("{id:int}/comments")]
    public async Task<IActionResult> AddComment(int id, AddCommentRequest request)
    {
        var result = await _cardService.AddCommentAsync(User.ToActor(), id, request.Text!);
        return result.Succeeded ? Ok(await ChangedAsync(id)) : this.ToProblem(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _cardService.DeleteAsync(User.ToActor(), id);

        if (!result.Succeeded)
            return this.ToProblem(result);

        await _boardNotifier.BoardChangedAsync(result.Value);
        return NoContent();
    }

    [HttpPut("{id:int}/labels/{labelId:int}")]
    public async Task<IActionResult> AddLabel(int id, int labelId)
    {
        var result = await _cardService.AddLabelAsync(User.ToActor(), id, labelId);
        return result.Succeeded ? Ok(await ChangedAsync(id)) : this.ToProblem(result);
    }

    [HttpDelete("{id:int}/labels/{labelId:int}")]
    public async Task<IActionResult> RemoveLabel(int id, int labelId)
    {
        var result = await _cardService.RemoveLabelAsync(User.ToActor(), id, labelId);
        return result.Succeeded ? Ok(await ChangedAsync(id)) : this.ToProblem(result);
    }

    [HttpGet("{id:int}/links")]
    public async Task<ActionResult<List<CardLinkDto>>> Links(int id) =>
        await _cardService.GetLinksAsync(User.ToActor(), id) is { } links ? links : NotFound();

    /// <summary>
    /// Links this card to another, e.g. { "target": "TCK-12", "category": "blocks" }.
    /// </summary>
    [HttpPost("{id:int}/links")]
    public async Task<IActionResult> AddLink(int id, AddLinkRequest request)
    {
        if (await _cardService.ResolveAsync(User.ToActor(), request.Target!) is not { } target)
            return Problem(detail: $"Target card '{request.Target}' not found.", statusCode: StatusCodes.Status404NotFound);

        var result = await _cardService.AddLinkAsync(User.ToActor(), id, target.Id, request.Category!);

        if (!result.Succeeded)
            return this.ToProblem(result);

        await _boardNotifier.BoardChangedAsync(target.BoardId);
        return Ok(await ChangedAsync(id));
    }

    /// <summary>
    /// Removes the link to another card (both directions). The target can be a card id or key.
    /// </summary>
    [HttpDelete("{id:int}/links/{target}")]
    public async Task<IActionResult> RemoveLink(int id, string target)
    {
        if (await _cardService.ResolveAsync(User.ToActor(), target) is not { } targetCard)
            return Problem(detail: $"Target card '{target}' not found.", statusCode: StatusCodes.Status404NotFound);

        var result = await _cardService.RemoveLinkAsync(User.ToActor(), id, targetCard.Id);

        if (!result.Succeeded)
            return this.ToProblem(result);

        await _boardNotifier.BoardChangedAsync(targetCard.BoardId);
        return Ok(await ChangedAsync(id));
    }

    private async Task<CardDto> ChangedAsync(int cardId)
    {
        var card = (await _cardService.GetAsync(User.ToActor(), cardId))!;
        await _boardNotifier.BoardChangedAsync(card.BoardId);
        return card;
    }
}
