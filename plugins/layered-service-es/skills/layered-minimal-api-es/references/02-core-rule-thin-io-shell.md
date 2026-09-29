# 02 — Regla central: shell de I/O delgado, lógica testeable

## La regla

**Toda clase que abre I/O real (SQL, cola, HTTP, un SDK de proveedor) es un shell delgado. La lógica
que necesita vive en clases puras separadas que se testean por unidad sin red, sin base de datos y sin
cuenta en ninguna parte.**

Por qué: el shell es la parte que no puedes testear, así que mantenla lo más pequeña posible. La lógica
es la parte que realmente tiene bugs, así que tiene que ser la parte que puedes testear de forma
exhaustiva y gratis. Cada regla de negocio enterrada en un shell es una regla que solo queda cubierta
por un test de integración que no puedes correr localmente.

Hazte una pregunta cada vez que una clase crezca: **¿qué líneas seguirían valiendo la pena testear si
no existieran la cadena de conexión ni la red?** Mueve esas líneas.

## Tres formas, un patrón

### Shell de cola vs. orquestación

```csharp
// SHELL — la única clase que sabe que existe una cola.
public sealed class QueueService(IOptions<QueueOptions> options) : IQueueService
{
    private readonly QueueClient _ordersQueue = new(options.Value.ConnectionString, options.Value.OrdersQueueName);
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private volatile bool _created;

    public async Task EnqueueOrderAsync(OrderMessage message, CancellationToken cancellationToken)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        await _ordersQueue.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);
    }

    private async Task EnsureQueueExistsAsync(CancellationToken cancellationToken)
    {
        if (_created) return;
        await _createLock.WaitAsync(cancellationToken);
        try
        {
            if (_created) return;
            await _ordersQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _created = true;
        }
        finally { _createLock.Release(); }
    }
}
```

```csharp
// LÓGICA — pura, sin I/O, testeable de forma exhaustiva.
public sealed class OrderNormalizer(TimeProvider timeProvider, OrderTelemetry telemetry)
{
    public NormalizedOrder Normalize(ReceivedOrder received, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(received);

        return new NormalizedOrder(
            correlationId,
            received.Reference.Trim().ToUpperInvariant(),
            received.Lines.Select(MapLine).ToList(),
            timeProvider.GetUtcNow());
    }

    private static NormalizedLine MapLine(ReceivedLine line) => new(
        line.Sku.Trim(),
        line.Quantity <= 0 ? throw new FormatException($"Quantity must be positive for SKU {line.Sku}.") : line.Quantity,
        line.UnitPrice);
}
```

El handler depende de `IQueueService` (el puerto) y de `OrderNormalizer` (la lógica). En los tests, el
puerto es un fake escrito a mano y el normalizador es la clase real.

### Shell de SDK vs. mapper de respuesta

```csharp
// SHELL — tiene el cliente del proveedor y no traduce nada.
public sealed class CarrierClient(CarrierApiClient api) : ICarrierService
{
    public async Task<ShipmentStatus> GetStatusAsync(string referenceCode, CancellationToken cancellationToken)
    {
        var raw = await api.GetShipmentAsync(referenceCode, cancellationToken);
        return ShipmentStatusMapper.Map(raw);          // llamada pura, aquí no hay lógica
    }
}
```

```csharp
// LÓGICA — función pura de tipos del SDK a tipos de dominio.
public static class ShipmentStatusMapper
{
    public static ShipmentStatus Map(CarrierShipment raw) => new(
        raw.Code.Trim().ToUpperInvariant(),
        TranslateState(raw.State),
        raw.UpdatedAt,
        raw.CarrierName ?? "unknown");

    public static OrderState TranslateState(string? carrierState) => carrierState switch
    {
        "CREATED" => OrderState.Received,
        "IN_TRANSIT" => OrderState.InTransit,
        "DELIVERED" => OrderState.Delivered,
        _ => OrderState.Unknown,
    };
}
```

El mapper es el archivo con más tests reales de toda la integración, y corre en microsegundos.

### Shell de escritura por lotes vs. acumulador

```csharp
// SHELL — drena un canal, llama a SQL. Nunca deja que un fallo escape.
public sealed class AuditBatchWriter(
    Channel<AuditLogEntry> channel,
    IAuditLogContext context,
    TimeProvider timeProvider,
    ILogger<AuditBatchWriter> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly BatchAccumulator<AuditLogEntry> _accumulator =
        new(BatchSize, FlushInterval, WriteBatchAsync, timeProvider, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try { await _accumulator.AddAsync(entry, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Telemetry.AuditErrors.Add(1, new KeyValuePair<string, object?>("reason", "flush_failed"));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _accumulator.DisposeAsync();     // vacía lo que queda
        await base.StopAsync(cancellationToken);
    }

    private async Task WriteBatchAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken cancellationToken) =>
        await context.WriteBatchAsync(batch, timeProvider.GetUtcNow(), cancellationToken);
}
```

```csharp
// LÓGICA — política de buffer y flush, sin I/O alguno.
public sealed class BatchAccumulator<T> : IAsyncDisposable
{
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flushAsync;
    private readonly List<T> _buffer;
    private readonly Lock _lock = new();
    private readonly ITimer _flushTimer;
    private readonly ILogger _logger;

    public BatchAccumulator(int batchSize, TimeSpan flushInterval,
        Func<IReadOnlyList<T>, CancellationToken, Task> flushAsync,
        TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _batchSize = batchSize;
        _flushAsync = flushAsync;
        _buffer = new List<T>(batchSize);
        _logger = logger ?? NullLogger.Instance;
        _flushTimer = (timeProvider ?? TimeProvider.System)
            .CreateTimer(OnTimerElapsed, null, flushInterval, flushInterval);
    }

    public async Task AddAsync(T item, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<T>? toFlush = null;
        lock (_lock)
        {
            _buffer.Add(item);
            if (_buffer.Count >= _batchSize) { toFlush = _buffer.ToArray(); _buffer.Clear(); }
        }
        if (toFlush is { Count: > 0 }) await FlushBatchAsync(toFlush, cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default) { /* misma forma */ }

    private async Task FlushBatchAsync(IReadOnlyList<T> batch, CancellationToken cancellationToken)
    {
        // El lote ya salió del buffer: si el flush falla se pierde, así que se loguea y se cuenta
        // ANTES de relanzar — el callback del timer no tiene a quién más avisarle.
        try { await _flushAsync(batch, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Telemetry.BatchFlushErrors.Add(1);
            _logger.LogError(ex, "Batch flush failed for {Count} items; they are lost.", batch.Count);
            throw;
        }
    }

    private void OnTimerElapsed(object? state) => _ = FlushOnTimerAsync();

    public async ValueTask DisposeAsync()
    {
        await _flushTimer.DisposeAsync();
        await FlushAsync();
    }
}
```

`BatchAccumulator<T>` es la clase de más valor del kit: una política de concurrencia difícil de
acertar, testeada en tests unitarios con un `flushAsync` falso y un `TimeProvider` falso.

## Corolarios innegociables

- **Puertos en `Application.UseCases/IServices/`, implementaciones en `Api/Services/`.** El handler
  depende de la interfaz; el SDK nunca aparece en una capa por debajo de `Api`.
- **El puerto es la costura que usan los tests.** Una interfaz por integración, angosta, con solo las
  operaciones que los handlers realmente llaman.
- **La clase pura devuelve tipos de dominio**, nunca tipos del SDK ni de persistencia. Eso es lo que
  la hace reutilizable por los tests sin el paquete.

## Anti-patrones

- `if (response.State == "IN_TRANSIT")` dentro de la clase cliente del SDK.
- Un handler que además consulta SQL *y* decide qué significa un 409.
- Una clase «de lógica» que recibe un `HttpClient`, un `DbContext` o un `QueueClient` por constructor.
- Un mapper que devuelve el DTO del proveedor y deja que el endpoint decida el código de estado.
- Testear solo a través de una base real porque «la parte pura es trivialmente correcta» — no lo es, y
  la parte pura es donde está el costo de un bug.
- Dejar la clase pura como `internal` y agregar `InternalsVisibleTo` en vez de Simply hacerla `public`.
