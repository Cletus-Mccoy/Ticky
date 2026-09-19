namespace Ticky.Internal.Services;

public enum ServiceError
{
    NotFound,
    Forbidden,
    Invalid,
    ColumnFull,
}

public record ServiceResult<T>(T? Value, ServiceError? Error = null, string? Message = null)
{
    public bool Succeeded => Error is null;

    public static ServiceResult<T> Ok(T value) => new(value);

    public static ServiceResult<T> Fail(ServiceError error, string message) => new(default, error, message);
}
