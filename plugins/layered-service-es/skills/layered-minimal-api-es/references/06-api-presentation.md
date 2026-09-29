# 06 — Presentación de la API

## Orden en `Program.cs`

El orden no es cosmético: cada paso depende de que el anterior ya esté configurado.

```csharp
using Contoso.Orders.Application.UseCases;
using Contoso.Orders.Infrastructure.Persistence;
using Contoso.Orders.Api.Endpoints;
using Contoso.Orders.Api.RateLimiting;
using Contoso.Orders.Api.Security;
using Contoso.Orders.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddObservability(builder.Configuration);       // 1. telemetría primero: todo lo que sigue queda instrumentado

builder.Services.AddPersistence(builder.Configuration);         // 2. capas
builder.Services.AddApplicationServices();

builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton<IQueueService, QueueService>();   // 3. puertos propiedad de Api
builder.Services.AddCarrier(builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];
        options.Audience = jwt["Audience"];
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(CarrierClientPolicy.Name, policy =>
        policy.Requirements.Add(new RequiredClientIdRequirement(jwt["RequiredClientId"])));
builder.Services.AddSingleton<IAuthorizationHandler, RequiredClientIdHandler>();

var rateLimit = builder.Configuration.GetSection(RateLimitSettings.SectionName);
builder.Services.Configure<RateLimitSettings>(rateLimit);
var rateLimitSettings = rateLimit.Get<RateLimitSettings>() ?? new RateLimitSettings();
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter =
            ((int)rateLimitSettings.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
    options.AddPolicy(OrderIngestRateLimiterPolicy.PolicyName,
        _ => OrderIngestRateLimiterPolicy.CreatePartition(rateLimitSettings));
});

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", false);
if (openApiEnabled) builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();                                       // 4. el más externo
app.UseRateLimiter();                                           // 5. partición global, así que antes de la autenticación: las avalanchas nunca llegan a la criptografía

if (openApiEnabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();                                        // 6. autenticación antes de autorización
app.UseAuthorization();

app.MapGroup("/orders").MapOrderEndpoints();
app.MapHealthEndpoints();

await app.RunAsync();
```

- **Sin `AddControllers()`.** Solo Minimal APIs.
- **Sin `AddCors`/`UseCors`.** Ver `references/07-security-and-rate-limiting.md`.
- **Sin `AddEndpointsApiExplorer()`.** Era un requisito de Swashbuckle; con
  `Microsoft.AspNetCore.OpenApi` la metadata de los endpoints ya está expuesta.
- **Scalar y el documento OpenAPI están condicionados a `OpenApi:Enabled`** y ese flag viene apagado
  por defecto — el `appsettings.json` del repositorio lo tiene en `false` y solo local/QA lo enciende.
- `/health` es `.AllowAnonymous()` y está excluido del filtro de métricas.

## Endpoints del módulo

```csharp
namespace Contoso.Orders.Api.Endpoints;

public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder builder)
    {
        builder.MapPost("/ingest", IngestAsync)
            .AddEndpointFilter<ValidationFilter<ReceivedOrder>>()
            .RequireAuthorization(OrderClientPolicy.Name)
            .RequireRateLimiting(OrderIngestRateLimiterPolicy.PolicyName)
            .WithSummary("Receives an order, normalizes it and enqueues it.");

        builder.MapGet("/shipments/{referenceCode}", GetShipmentStatusAsync)
            .AddEndpointFilter<IdValidationFilter>()
            .RequireRateLimiting(CarrierQueryRateLimiterPolicy.PolicyName)
            .WithSummary("Returns the shipment status from the carrier.");

        return builder;
    }

    private static async Task<IResult> IngestAsync(
        ReceivedOrder request, IOrderHandler handler, CancellationToken cancellationToken) =>
        HandlingHelper.ToHttpResult(await handler.HandleAsync(request, cancellationToken));

    private static async Task<IResult> GetShipmentStatusAsync(
        string referenceCode, IShipmentStatusHandler handler, CancellationToken cancellationToken) =>
        HandlingHelper.ToHttpResult(await handler.HandleAsync(referenceCode, cancellationToken));
}
```

Cada grupo es `app.MapGroup("/orders").Map<Module>Endpoints()`. Agregar un módulo significa agregar
un archivo en `Endpoints/` y una línea en `Program.cs` — no editar un switch que crece en algún lado.

Devuelve `IResult` cuando el helper hace el mapeo, o una unión `Results<...>` cuando el endpoint tiene
formas distintas y quieres que el schema OpenAPI las muestre. **Nunca `IActionResult`**: oculta el
contrato frente al documento OpenAPI y frente al compilador.

## Validación del body

```csharp
namespace Contoso.Orders.Api.Filters;

public sealed class ValidationFilter<T> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var target = context.Arguments.OfType<T>().FirstOrDefault();
        if (target is null) return Results.BadRequest();

        var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null) return await next(context);      // sin validador registrado: no es un error

        var result = await validator.ValidateAsync(target);
        return result.IsValid ? await next(context) : Results.ValidationProblem(result.ToDictionary());
    }
}
```

`Results.ValidationProblem` produce un body `ProblemDetails` con un diccionario `errors` — la misma
forma que usa el framework, así que los clientes necesitan un solo modelo de error.

## Validación de parámetros de ruta — aparte, y no es lo mismo

`{referenceCode}` en una ruta es un `string`; si debe ser un `Guid`, el model binding no lo va a
impedir.

```csharp
public sealed class IdValidationFilter : IEndpointFilter
{
    private static readonly GuidValidator Validator = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<string>().LastOrDefault() is { Length: > 0 } raw
            && !Guid.TryParse(raw, out _))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = ["The route parameter must be a valid identifier."],
            });
        }

        return await next(context);
    }
}
```

Dos filtros a propósito: la validación del body es una preocupación por DTO que crece con los
endpoints; la validación de parámetros de ruta es una forma repetida en todas partes.

## Mapeo de Result a HTTP, en un solo lugar

```csharp
namespace Contoso.Orders.Api.Commons;

public static class HandlingHelper
{
    public static IResult ToHttpResult(OperationResultDto result) => result.CodeHttp switch
    {
        "202" => Results.Accepted(),
        "503" => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status500InternalServerError),
    };

    public static IResult ToHttpResult<T>(OperationResultDto<T> result) => result.CodeHttp switch
    {
        "200" => Results.Ok(result.Data),
        "400" => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status400BadRequest),
        "404" => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status404NotFound),
        "409" => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status409Conflict),
        "429" => new RetryAfterResult(
            Results.Problem(detail: result.Message, statusCode: StatusCodes.Status429TooManyRequests),
            result.RetryAfter),
        "503" => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status500InternalServerError),
    };

    private sealed class RetryAfterResult(IResult inner, TimeSpan? retryAfter)
        : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => (inner as IStatusCodeHttpResult)?.StatusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (retryAfter is { } delay)
                httpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
```

## Excepciones no manejadas

```csharp
namespace Contoso.Orders.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // El JSON malformado lo detecta el model binding, antes de que corra el handler.
        var (status, title, detail) = exception switch
        {
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request",
                                        "The request body could not be read."),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested =>
                (StatusCodes.Status499ClientClosedRequest, "Client closed request", "The client cancelled the request."),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error",
                  "An unexpected error occurred."),
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception on {Method} {Path}.",
            context.Request.Method, context.Request.Path);
        else logger.LogWarning(exception, "Rejected request on {Method} {Path}.",
            context.Request.Method, context.Request.Path);

        await Results.Problem(detail: detail, statusCode: status, title: title)
            .ExecuteAsync(context);

        return true;
    }
}
```

`IExceptionHandler` en vez de un middleware `try/catch`, porque es el punto de extensión del propio
framework: compone con `AddProblemDetails()` y con las páginas de código de estado, y no se traga la
respuesta si ya empezó. Se registra **antes** que cualquier otro middleware para que envuelva todo.

## Anti-patrones

- `app.UseCors("AllowAll")` en una API servidor a servidor.
- `app.MapControllers()` en un proyecto de minimal API.
- Un validador de JWT hecho a mano en `Security/`.
- `Results.Problem(detail: ex.Message, ...)` para una excepción no manejada.
- Validar el body dentro del handler en vez de con un filtro.
- OpenAPI + Scalar mapeados sin condición, exponiendo la superficie en producción.
- Un `Endpoints.cs` gigante con todas las rutas dentro.
- Tipos de retorno `IActionResult`.
