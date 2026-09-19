using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticky.Internal.Data;

namespace Ticky.Units.Services;

/// <summary>
/// In-memory SQLite database with the real DataContext model, so service tests run without MySQL.
/// </summary>
public sealed class SqliteDbFixture : IDbContextFactory<DataContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DataContext> _options;

    public SqliteDbFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DataContext>().UseSqlite(_connection).Options;

        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public DataContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();

    public record Seed(User Member, User Outsider, Board Board, Board OtherBoard, Column Todo, Column Doing, Column Done, Column OtherColumn);

    /// <summary>
    /// Member has access to Board (via project) and OtherBoard (via board membership). Outsider has none.
    /// </summary>
    public Seed SeedBoards(int doingMaxCards = 0)
    {
        using var db = CreateDbContext();

        var member = new User { DisplayName = "Member", UserName = "member", Email = "member@x" };
        var outsider = new User { DisplayName = "Outsider", UserName = "outsider", Email = "outsider@x" };
        db.Users.AddRange(member, outsider);
        db.SaveChanges();

        var project = new Project { Name = "P" };
        db.Projects.Add(project);
        db.SaveChanges();
        db.ProjectMemberships.Add(new ProjectMembership { ProjectId = project.Id, UserId = member.Id, IsAdmin = true, AddedAt = DateTime.Now });

        var otherProject = new Project { Name = "Q" };
        db.Projects.Add(otherProject);
        db.SaveChanges();

        var board = new Board { Name = "Board", Description = "", Code = "TST", ProjectId = project.Id };
        var otherBoard = new Board { Name = "Other", Description = "", Code = "OTH", ProjectId = otherProject.Id };
        db.Boards.AddRange(board, otherBoard);
        db.SaveChanges();
        db.BoardMemberships.Add(new BoardMembership { BoardId = otherBoard.Id, UserId = member.Id, IsAdmin = false, AddedAt = DateTime.Now });

        var todo = new Column { Name = "Todo", BoardId = board.Id, Index = 0 };
        var doing = new Column { Name = "Doing", BoardId = board.Id, Index = 1, MaxCards = doingMaxCards };
        var done = new Column { Name = "Done", BoardId = board.Id, Index = 2, Finished = true };
        var otherColumn = new Column { Name = "Elsewhere", BoardId = otherBoard.Id, Index = 0 };
        db.Columns.AddRange(todo, doing, done, otherColumn);
        db.SaveChanges();

        return new Seed(member, outsider, board, otherBoard, todo, doing, done, otherColumn);
    }
}
