namespace Ticky.Web.Api;

public static class ServiceResultExtensions
{
    public static IActionResult ToProblem<T>(this ControllerBase controller, ServiceResult<T> result) =>
        controller.Problem(
            detail: result.Message,
            statusCode: result.Error switch
            {
                ServiceError.NotFound => StatusCodes.Status404NotFound,
                ServiceError.ColumnFull => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            }
        );
}
