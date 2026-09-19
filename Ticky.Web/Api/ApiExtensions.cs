namespace Ticky.Web.Api;

public static class ApiExtensions
{
    /// <summary>
    /// Builds the service-layer actor from a principal authenticated by <see cref="ApiTokenAuthenticationHandler"/>.
    /// </summary>
    public static Actor ToActor(this ClaimsPrincipal principal)
    {
        var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var scope = principal.FindFirstValue(ApiTokenAuthenticationHandler.BOARD_SCOPE_CLAIM);

        return new Actor(userId, scope is null ? null : int.Parse(scope));
    }
}
