namespace Ticky.Internal.Services;

/// <summary>
/// Who is performing an operation. <see cref="BoardScopeId"/> is set for API tokens
/// restricted to a single board; interactive users have no scope.
/// </summary>
public record Actor(int UserId, int? BoardScopeId = null);
