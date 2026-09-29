# 06 — API presentation

## `Program.cs` order

The order is not cosmetic: each step depends on the previous one having been configured.

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

builder.Services.AddObservability(builder.Configuration);       // 1. telemetry first: everything after is instrumented

builder.Services.AddPersistence(builder.Configuration);         // 2. layers
builder.Services.AddApplicationServices();

builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton<IQueueService, QueueService>();   // 3. Api-owned ports
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

app.UseExceptionHandler();                                       // 4. outermost
app.UseRateLimiter();                                           // 5. global partition, so before auth: floods never reach crypto

if (openApiEnabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();                                        // 6. auth before authorization
app.UseAuthorization();

app.MapGroup("/orders").MapOrderEndpoints();
app.MapHealthEndpoints();

await app.RunAsync();
```

- **No `AddControllers()`.** Minimal APIs only.
- **No `AddCors`/`UseCors`.** See `references/07-security-and-rate-limiting.md`.
- **No `AddEndpointsApiExplorer()`.** It was a Swashbuckle requirement; with `Microsoft.AspNetCore.OpenApi`
  the endpoint metadata is already exposed.
- **Scalar and the OpenAPI document are gated on `OpenApi:Enabled`** and that flag is off by default —
  the committed `appsettings.json` has it `false` and only local/QA turn it on.
- `/health` is `.AllowAnonymous()` and excluded from the metrics filter.

## Module endpoints

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

Every group is `app.MapGroup("/orders").Map<Module>Endpoints()`. Adding a module means adding one file
in `Endpoints/` and one line in `Program.cs` — not editing a growing switch somewhere.

Return `IResult` when the helper does the mapping, or a `Results<...>` union when the endpoint has
distinct shapes and you want the OpenAPI schema to show them. **Never `IActionResult`**: it hides the
contract from the OpenAPI document and from the compiler.

## Body validation

```csharp
namespace Contoso.Orders.Api.Filters;

public sealed class ValidationFilter<T> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var target = context.Arguments.OfType<T>().FirstOrDefault();
        if (target is null) return Results.BadRequest();

        var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null) return await next(context);      // no validator registered: not a mistake

        var result = await validator.ValidateAsync(target);
        return result.IsValid ? await next(context) : Results.ValidationProblem(result.ToDictionary());
    }
}
```

`Results.ValidationProblem` produces a `ProblemDetails` body with an `errors` dictionary — the same
shape the framework uses, so clients need one error model.

## Route param validation — separate, and it is not the same thing

`{referenceCode}` in a route is a `string`; if it must be a `Guid`, model binding will not stop it.

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

Two filters on purpose: body validation is a per-DTO concern that scales with the endpoints, route
param validation is one shape repeated everywhere.

## Result → HTTP mapping, in one place

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

## Unhandled exceptions

```csharp
namespace Contoso.Orders.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Malformed JSON is detected by model binding, before the handler runs.
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

`IExceptionHandler` rather than a `try/catch` middleware, because it is the framework's own extension
point: it composes with `AddProblemDetails()` and with the status-code pages, and it does not swallow
the response if it has already started. Registered **before** any other middleware so it wraps
everything.

## Anti-patterns

- `app.UseCors("AllowAll")` in a server-to-server API.
- `app.MapControllers()` in a minimal API project.
- A hand-rolled JWT validator in `Security/`.
- `Results.Problem(detail: ex.Message, ...)` for an unhandled exception.
- Validating the body inside the handler instead of with a filter.
- OpenAPI + Scalar mapped unconditionally, exposing the surface in production.
- One giant `Endpoints.cs` with every route in it.
- `IActionResult` return types.
