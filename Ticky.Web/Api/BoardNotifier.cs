namespace Ticky.Web.Api;

/// <summary>
/// Tells open board views to refresh after a change made outside the UI (REST API, MCP).
/// </summary>
public class BoardNotifier
{
    private readonly IHubContext<UpdateHub> _hubContext;

    public BoardNotifier(IHubContext<UpdateHub> hubContext)
    {
        _hubContext = hubContext;
    }

    // Guid.Empty never matches a browser's page id, so every subscriber reloads
    public Task BoardChangedAsync(int boardId) =>
        _hubContext.Clients.Group($"board_{boardId}").SendAsync(nameof(UpdateHub.BoardChange), boardId, Guid.Empty);
}
