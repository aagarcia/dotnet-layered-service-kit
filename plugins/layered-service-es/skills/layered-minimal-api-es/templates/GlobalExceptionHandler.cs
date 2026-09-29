// El punto de extensión del propio framework para las excepciones no controladas, registrado con
// builder.Services.AddExceptionHandler<GlobalExceptionHandler>() y app.UseExceptionHandler().
// Prefiérelo sobre un middleware con try/catch: se compone con AddProblemDetails(), y no se traga
// una respuesta que ya empezó.
//
// Ver references/06-api-presentation.md.
//
// HttpContext, BadHttpRequestException, StatusCodes e ILogger<T> llegan por los usings implícitos
// del Web SDK. IExceptionHandler no: necesita Microsoft.AspNetCore.Diagnostics.
using Microsoft.AspNetCore.Diagnostics;

namespace Contoso.Orders.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    // Los nombres de los parámetros siguen exactamente la declaración de la interfaz: S927 marca un
    // renombrado, y un desajuste aquí es lo que hace difícil de leer un override contra la docs.
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Con Minimal APIs un body JSON malformado lo captura el model binding, antes de que corra el
        // handler, y aparece como BadHttpRequestException. Sin este mapeo, quien llama obtendría un
        // 400 sin body, en lugar de un ProblemDetails que pueda parsear.
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

        // Nunca devuelvas exception.Message a quien llama: filtra detalles internos y pasa a formar
        // parte de tu contrato público en el momento en que un cliente depende de ella.
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
