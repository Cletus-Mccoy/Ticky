namespace Ticky.Internal.Helpers;

public static class AccessHelper
{
    /// <summary>
    /// Boards the actor may see: a board or project membership, narrowed to the token's board scope if any.
    /// Boards outside this set are reported as not found so their existence is not leaked.
    /// </summary>
    public static IQueryable<Board> AccessibleBoards(this DataContext db, Actor actor) =>
        db.Boards.Where(b =>
            (actor.BoardScopeId == null || b.Id == actor.BoardScopeId)
            && (
                b.Memberships.Any(m => m.UserId == actor.UserId)
                || b.Project.Memberships.Any(m => m.UserId == actor.UserId)
            )
        );

    /// <summary>
    /// Mirrors the UI's admin check: the board membership decides if there is one, otherwise the project membership.
    /// </summary>
    public static IQueryable<Board> AdministeredBoards(this DataContext db, Actor actor) =>
        db.AccessibleBoards(actor)
            .Where(b =>
                b.Memberships.Any(m => m.UserId == actor.UserId)
                    ? b.Memberships.Any(m => m.UserId == actor.UserId && m.IsAdmin)
                    : b.Project.Memberships.Any(m => m.UserId == actor.UserId && m.IsAdmin)
            );

    public static IQueryable<Card> AccessibleCards(this DataContext db, Actor actor) =>
        db.Cards.Where(c => db.AccessibleBoards(actor).Any(b => b.Id == c.Column.BoardId));
}
