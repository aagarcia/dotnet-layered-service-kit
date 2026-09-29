# 05 — Application.UseCases

## La regla

Handlers en `Features/` (plano, uno por integración). Interfaces en `IFeatures/`. Puertos hacia
infraestructura en `IServices/`, **implementados en `Api`**. DTOs de entrada, validadores de
FluentValidation auto-registrados por assembly, config de Mapster por módulo. Registro en
`AddApplicationServices(services)`.

Por qué los puertos viven aquí y las implementaciones en `Api`: el handler debe ser testeable sin el
paquete del SDK instalado en el proyecto de pruebas y sin una cuenta activa. La interfaz es la costura;
la implementación es infraestructura.

## El resultado estándar

La mayoría de los resultados de un handler no son excepciones. Un handler devuelve un DTO de
resultado pequeño que lleva un código HTTP y un mensaje; el endpoint lo traduce a un resultado tipado.

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

Mantén el código como string y haz un switch sobre él en un solo helper. Por qué: el handler es una
biblioteca, no un servidor HTTP; dejarlo lanzar `BadHttpRequestException` lo acoplaría al transporte, y
dejar que cada handler construya sus propios `TypedResults` pondría el mapeo HTTP en veinte lugares en
vez de uno.

**No** devuelvas al llamador los mensajes de excepción en crudo — loguéalos, devuelve un mensaje
estable. Los códigos de arriba son el contrato; los mensajes no.

## DTOs

Un DTO de request por cada entrada de endpoint; un DTO de response por cada forma. `record`,
posicional, sin comportamiento.

```csharp
namespace Contoso.Orders.Application.UseCases.Commons.DTOs;

public sealed record ReceivedOrder(
    string Reference,
    IReadOnlyList<ReceivedLine> Lines,
    string? CustomerNote);

public sealed record ReceivedLine(string Sku, int Quantity, decimal UnitPrice);
```

Naming: `Received*` es lo que llega por el cable, `Normalized*` es lo que va a la cola. La distinción
es el contrato — ver `references/08-async-ingestion-202.md`.

## Validadores

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

Se auto-registran por assembly en `AddApplicationServices`. No los registres uno por uno: una lista
manual es una lista que en silencio se deja afuera el validador que se agregó la semana pasada.

Los parámetros de ruta **no** se validan aquí — un `Guid id` que no es un `Guid` falla en el model
binding. Eso es un filtro aparte, `IdValidationFilter`, descrito en
`references/06-api-presentation.md`.

## Mapeos — Mapster, una config por módulo

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

`MapsterGlobalConfig.Initialize()` se llama una vez, estáticamente, desde `AddApplicationServices`.
Como es estático, **no puede** usar un servicio inyectado — todo lo que dependa del tiempo o de las
opciones (incluidos «ahora» y el correlation id) se aplica en el handler después del mapeo, con
expresiones `with` o por asignación. `AddMapster()` registra `TypeAdapterConfig` como **singleton**.

## Puertos

```csharp
namespace Contoso.Orders.Application.UseCases.IServices;

public interface IQueueService
{
    Task EnqueueOrderAsync(OrderMessage message, CancellationToken cancellationToken = default);
}

public interface IAuditLogService
{
    void TryEnqueue(AuditLogEntry entry);      // sincrónico, no bloqueante, best effort
}
```

`TryEnqueue` es deliberadamente sincrónico y no bloqueante. El registro de auditoría corre dentro del
manejo del request y nunca debe agregar latencia ni un modo de fallo al flujo de negocio. La palabra
`Try` en el nombre es el contrato: no devuelve nada porque el llamador no hace nada con la respuesta.

## Un handler

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

        // La validación del body ya corrió en ValidationFilter<ReceivedOrder>; el handler no la repite.
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

Fíjate en el constructor primario. El código de producción aquí usa constructores primarios en todas
partes; una clase base con cuatro sobrecargas de constructor para elegir qué colaboradores opcionales
necesitas es un peor intercambio que listar los cuatro parámetros.

## Registro

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

        // Los mensajes de validación nombran el campo JSON, no la propiedad C#.
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

Tiempos de vida: los handlers **Scoped** (dependen de un `Context` de módulo, que es una unidad de
trabajo). Los fakes en las pruebas implementan las mismas interfaces y no toman decisiones de tiempo
de vida propias.

## Anti-patrones

- Una clase base `BaseHandler<TContext>` con sobrecargas de constructor para elegir colaboradores
  opcionales.
- Handlers que lanzan `BadHttpRequestException` / `NotFoundException` en vez de devolver un resultado.
- Mensajes de excepción devueltos textualmente al llamador.
- Puertos declarados en `Api` en vez de `Application.UseCases/IServices/`.
- El paquete del SDK referenciado desde `Application.UseCases`.
- Validadores registrados uno por uno, o un validador que duplica lo que el model binding ya impone.
- Un `TypeAdapterConfig` estático que llama a `DateTime.Now` o resuelve una opción inyectada.
- Un handler que vuelve a validar el body porque no confía en el filtro.
- Subcarpetas por integración bajo `Features/`.
- `services.AddSingleton(TimeProvider.System)` agregado en tres capas distintas.
