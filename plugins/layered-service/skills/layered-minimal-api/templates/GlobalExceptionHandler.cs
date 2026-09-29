// The framework's own extension point for unhandled exceptions, registered with
// builder.Services.AddExceptionHandler<GlobalExceptionHandler>() and app.UseExceptionHandler().
// Prefer it over a try/catch middleware: it composes with AddProblemDetails(), and it does not
// swallow a response that has already started.
//
// See references/06-api-presentation.md.
//
// HttpContext, BadHttpRequestException, StatusCodes and ILogger<T> arrive through the Web SDK's
// implicit usings. IExceptionHandler does not: it needs Microsoft.AspNetCore.Diagnostics.
using Microsoft.AspNetCore.Diagnostics;

namespace Contoso.Orders.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    // Parameter names follow the interface declaration exactly: S927 flags a rename, and a
    // mismatch here is what makes an override hard to read against the docs.
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // With minimal APIs a malformed JSON body is caught by model binding, before the handler
        // runs, and surfaces as BadHttpRequestException. Without this mapping the caller would get
        // a bare 400 with no body, instead of a ProblemDetails it can parse.
        var (status, title, detail) = exception switch
        {
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "The request body could not be read."),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (
                StatusCodes.Status499ClientClosedRequest,
                "Client closed request",
                "The client cancelled the request."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An unexpected error occurred."),
        };

        // Never return exception.Message to the caller: it leaks internals and becomes part of
        // your public contract the moment a client depends on it.
        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Rejected request on {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);
        }

        await Results.Problem(detail: detail, statusCode: status, title: title)
            .ExecuteAsync(httpContext);

        return true;
    }
}
