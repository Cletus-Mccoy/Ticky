namespace Ticky.Base.DTOs.Api;

public record BoardSummaryDto(int Id, string Code, string Name, string ProjectName);

public record ColumnDto(int Id, string Name, int Index, bool Finished, int MaxCards, int CardCount);

public record BoardDto(int Id, string Code, string Name, string Description, List<ColumnDto> Columns, List<CardSummaryDto> Cards);

public record CardSummaryDto(int Id, string Key, string Name, int ColumnId, string ColumnName, int Index, CardPriority Priority, DateTime? Deadline, bool Flagged, List<string> Assignees, List<string> Labels);

public record CardDto(
    int Id,
    string Key,
    int BoardId,
    string Name,
    string Description,
    int ColumnId,
    string ColumnName,
    bool Finished,
    int Index,
    CardPriority Priority,
    DateTime? Deadline,
    bool Flagged,
    string CreatedBy,
    DateTime CreatedAt,
    List<string> Assignees,
    List<string> Labels,
    List<CommentDto> Comments
);

public record CommentDto(int Id, string Author, string Text, DateTime CreatedAt);

public record ActivityDto(
    int Id,
    int CardId,
    string CardKey,
    string CardName,
    ActivityType Type,
    string Text,
    string User,
    DateTime CreatedAt,
    string? FromColumn,
    string? ToColumn,
    bool? ToColumnFinished
);

public record ColumnStatsDto(int Id, string Name, bool Finished, int Cards);

public record BurndownPointDto(DateOnly Date, int Open);

public record BoardStatsDto(int BoardId, int Total, int Done, int Open, List<ColumnStatsDto> Columns, List<BurndownPointDto> Burndown);

public record CreateCardRequest([property: Required] int? ColumnId, [property: Required] string? Name, string? Description);

public record UpdateCardRequest(string? Name, string? Description);

public record MoveCardRequest([property: Required] int? ColumnId, int? Index, int? BeforeCardId);

public record AddCommentRequest([property: Required] string? Text);
