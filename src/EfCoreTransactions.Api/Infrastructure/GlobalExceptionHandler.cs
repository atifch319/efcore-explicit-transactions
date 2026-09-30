using EfCoreTransactions.Api.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace EfCoreTransactions.Api.Infrastructure;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            exception,
            "Request failed for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var (statusCode, title, detail) = exception switch
        {
            InsufficientInventoryException => (
                StatusCodes.Status409Conflict,
                "Insufficient inventory.",
                exception.Message),
            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "The requested product was not found.",
                "The product does not exist."),
            ArgumentException => (
                StatusCodes.Status400BadRequest,
                "The order request is invalid.",
                "The order request is invalid."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "The order workflow failed.",
                "The operation could not be completed.")
        };

        await Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            instance: httpContext.Request.Path)
            .ExecuteAsync(httpContext);

        return true;
    }
}
