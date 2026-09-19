using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ticky.Web.Api;

/// <summary>
/// Authenticates "Authorization: Bearer tky_..." requests against stored API tokens.
/// </summary>
public class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SCHEME = "ApiToken";
    public const string POLICY = "ApiToken";
    public const string BOARD_SCOPE_CLAIM = "ticky:board_scope";

    private readonly ApiTokenService _apiTokenService;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiTokenService apiTokenService
    )
        : base(options, logger, encoder)
    {
        _apiTokenService = apiTokenService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;

        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var actor = await _apiTokenService.ValidateAsync(header["Bearer ".Length..].Trim());

        if (actor is null)
            return AuthenticateResult.Fail("Invalid, expired or revoked API token.");

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.UserId.ToString()) };

        if (actor.BoardScopeId is not null)
            claims.Add(new(BOARD_SCOPE_CLAIM, actor.BoardScopeId.Value.ToString()));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SCHEME));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SCHEME));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
