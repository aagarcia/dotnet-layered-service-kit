# 05 — Application.UseCases

## The rule

Handlers in `Features/` (flat, one per integration). Interfaces in `IFeatures/`. Ports to
infrastructure in `IServices/`, **implemented in `Api`**. Input DTOs, FluentValidation validators
auto-registered by assembly, Mapster config per module. Registration in
`AddApplicationServices(services)`.

Why the ports live here and the implementations in `Api`: the handler must be testable without the SDK
package installed in the test project, and without a live account. The interface is the seam; the
implementation is infrastructure.

## The standard result

Most handler outcomes are not exceptions. A handler returns a small result DTO carrying an HTTP code
and a message; the endpoint translates it into a typed result.

```csharp
namespace Contoso.Orders.Application.UseCases.Commons.DTOs;

public sealed record OperationResultDto(string CodeHttp, string Message)
{
    public static OperationResultDto Accepted() => new("202", "Accepted");

    public static OperationResultDto ServiceUnavailable(string message) => new("503", message);

    public static OperationResultDto InternalError(string message) => new("500", message);
}
```

```csharp
public sealed record OperationResultDto<T>(string CodeHttp, string Message, T? Data, TimeSpan? RetryAfter = null)
{
    public static OperationResultDto<T> Ok(T data) => new("200", "Ok", data);

    public static OperationResultDto<T> BadRequest(string message) => new("400", message, default);

    public static OperationResultDto<T> NotFound(string message) => new("404", message, default);

    public static OperationResultDto<T> Conflict(string message) => new("409", message, default);

    public static OperationResultDto<T> TooManyRequests(string message, TimeSpan retryAfter) =>
        new("429", message, default, retryAfter);

    public static OperationResultDto<T> ServiceUnavailable(string message) => new("503", message, default);
}
```

Keep the code as a string and switch on it in one helper. Why: the handler is a library, not an HTTP
server; letting it throw `BadHttpRequestException` would couple it to the transport, and letting every
handler build its own `TypedResults` would put the HTTP mapping in twenty places instead of one.

Do **not** return raw exception messages to the caller — log them, return a stable message. The codes
above are the contract; the messages are not.

## DTOs

One request DTO per endpoint input; one response DTO per shape. `record`, positional, no behavior.

```csharp
namespace Contoso.Orders.Application.UseCases.Commons.DTOs;

public sealed record ReceivedOrder(
    string Reference,
    IReadOnlyList<ReceivedLine> Lines,
    string? CustomerNote);

public sealed record ReceivedLine(string Sku, int Quantity, decimal UnitPrice);
```

Naming: `Received*` is what arrives on the wire, `Normalized*` is what goes on the queue. The
distinction is the contract — see `references/08-async-ingestion-202.md`.

## Validators

```csharp
namespace Contoso.Orders.Application.UseCases.Commons.Validations;

public sealed class ReceivedOrderValidator : AbstractValidator<ReceivedOrder>
{
    public ReceivedOrderValidator()
    {
        RuleFor(o => o.Reference).NotEmpty().MaximumLength(40);
        RuleFor(o => o.Lines).NotEmpty().Must(l => l.Count <= 100)
              .WithMessage("An order cannot contain more than 100 lines.");
        RuleForEach(o => o.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Sku).NotEmpty().MaximumLength(32);
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.UnitPrice).GreaterThan(0);
        });
    }
}
```

They auto-register by assembly in `AddApplicationServices`. Do not register them one by one: a manual
list is a list that silently misses the validator added last week.

Route params are **not** validated here — a `Guid id` that is not a `Guid` fails at model binding. That
is a separate filter, `IdValidationFilter`, described in `references/06-api-presentation.md`.

## Mappings — Mapster, one config per module

```csharp
namespace Contoso.Orders.Application.UseCases.Commons.Mappings;

public static class OrderMapsterConfig
{
    public static void Configure()
    {
        TypeAdapterConfig<ReceivedOrder, NormalizedOrder>.NewConfig()
            .MapWith(src => OrderNormalizer.Normalize(src));
    }
}
```

```csharp
public static class MapsterGlobalConfig
{
    public static void Initialize()
    {
        OrderMapsterConfig.Configure();
    }
}
```

`MapsterGlobalConfig.Initialize()` is called once, statically, from `AddApplicationServices`. Because
it is static it **cannot** use an injected service — anything time-dependent or option-dependent
(including "now" and the correlation id) is applied in the handler after the mapping, with `with`
expressions or by assignment. `TypeAdapterConfig` is registered as a **singleton** by `AddMapster()`.

## Ports

```csharp
namespace Contoso.Orders.Application.UseCases.IServices;

public interface IQueueService
{
    Task EnqueueOrderAsync(OrderMessage message, CancellationToken cancellationToken = default);
}

public interface IAuditLogService
{
    void TryEnqueue(AuditLogEntry entry);      // synchronous, non-blocking, best effort
}
```

`TryEnqueue` is deliberately synchronous and non-blocking. Audit logging runs inside request handling
and must never add latency or a failure mode to the business flow. The word `Try` in the name is the
contract: it returns nothing because the caller does nothing with the answer.

## A handler

```csharp
namespace Contoso.Orders.Application.UseCases.Features;

public sealed class OrderHandler(
    IQueueService queueService,
    IAuditLogService auditLogService,
    TimeProvider timeProvider,
    ILogger<OrderHandler> logger) : IOrderHandler
{
    public async Task<OperationResultDto> HandleAsync(ReceivedOrder request, CancellationToken cancellationToken)
    {
        using var activity = OrderTelemetry.ActivitySource.StartActivity("OrderHandler.Handle");
        var correlationId = Guid.CreateVersion7();
        activity?.SetTag("orders.correlation_id", correlationId);

        // Body validation already ran in ValidationFilter<ReceivedOrder>; the handler does not repeat it.
        var normalized = request.Adapt<NormalizedOrder>() with
        {
            CorrelationId = correlationId,
            NormalizedAt = timeProvider.GetUtcNow(),
        };

        try
        {
            await queueService.EnqueueOrderAsync(
                new OrderMessage(correlationId, normalized), cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Queue unavailable while enqueuing the order.");
            return Track(activity, correlationId, "queue_unavailable",
                OperationResultDto.ServiceUnavailable("Ingestion queue is temporarily unavailable."), ex.Message);
        }

        return Track(activity, correlationId, "accepted", OperationResultDto.Accepted());
    }

    private static OperationResultDto Track(
        Activity? activity, Guid correlationId, string outcome, OperationResultDto result, string? error = null)
    {
        activity?.SetTag("orders.outcome", outcome);
        if (outcome != "accepted") activity?.SetStatus(ActivityStatusCode.Error, outcome);

        OrderTelemetry.OrdersIngested.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        auditLogService.TryEnqueue(new AuditLogEntry(
            correlationId, "order", outcome, "api", TraceId: activity?.TraceId.ToString()));
        return result;
    }
}
```

Note the primary constructor. Production code here uses primary constructors throughout; a base class
with four constructor overloads to select which optional collaborators you need is a worse trade than
listing the four parameters.

## Registration

```csharp
public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IOrderHandler, OrderHandler>();
        services.AddScoped<IShipmentStatusHandler, ShipmentStatusHandler>();

        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
        services.AddSingleton(TimeProvider.System);

        MapsterGlobalConfig.Initialize();
        services.AddMapster();

        // Validation messages name the JSON field, not the C# property.
        ValidatorOptions.Global.DisplayNameResolver = (_, member, _) =>
        {
            if (member is null) return null;
            var attr = member.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                .FirstOrDefault() as JsonPropertyNameAttribute;
            return attr?.Name ?? member.Name;
        };

        return services;
    }
}
```

Lifetimes: handlers **Scoped** (they depend on a module `Context`, which is a unit of work). Fakes in
tests implement the same interfaces and take no lifetime decisions of their own.

## Anti-patterns

- A base class `BaseHandler<TContext>` with constructor overloads to pick optional collaborators.
- Handlers throwing `BadHttpRequestException` / `NotFoundException` instead of returning a result.
- Exception messages returned verbatim to the caller.
- Ports declared in `Api` instead of `Application.UseCases/IServices/`.
- The SDK package referenced from `Application.UseCases`.
- Validators registered one by one, or a validator that duplicates what model binding already enforces.
- A static `TypeAdapterConfig` that calls `DateTime.Now` or resolves an injected option.
- A handler that re-validates the body because it does not trust the filter.
- Subfolders per integration under `Features/`.
- `services.AddSingleton(TimeProvider.System)` added in three different layers.
