# 02 — Core rule: thin I/O shell, testable logic

## The rule

**Every class that opens real I/O (SQL, queue, HTTP, a vendor SDK) is a thin shell. The logic it needs
lives in separate pure classes that unit-test with no network, no database and no account anywhere.**

Why: the shell is the part you cannot test, so keep it as small as possible. The logic is the part that
actually has bugs, so it must be the part you can test exhaustively and for free. Every business rule
buried inside a shell is a rule that is only ever covered by an integration test you cannot run locally.

Ask one question when a class grows: **which lines would still be worth testing if the connection
string and the network did not exist?** Move those lines out.

## Three shapes, one pattern

### Queue shell vs. orchestration

```csharp
// SHELL — the only class that knows a queue exists.
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
// LOGIC — pure, no I/O, exhaustively testable.
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

The handler depends on `IQueueService` (the port) and `OrderNormalizer` (the logic). In tests, the port
is a hand-written fake and the normalizer is the real class.

### SDK shell vs. response mapper

```csharp
// SHELL — holds the vendor client, translates nothing.
public sealed class CarrierClient(CarrierApiClient api) : ICarrierService
{
    public async Task<ShipmentStatus> GetStatusAsync(string referenceCode, CancellationToken cancellationToken)
    {
        var raw = await api.GetShipmentAsync(referenceCode, cancellationToken);
        return ShipmentStatusMapper.Map(raw);          // pure call, no logic here
    }
}
```

```csharp
// LOGIC — pure function from SDK types to domain types.
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

The mapper is the file with the most real tests in the whole integration, and it runs in microseconds.

### Batch writer shell vs. batch accumulator

```csharp
// SHELL — drains a channel, calls SQL. Never lets a failure escape.
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
        await _accumulator.DisposeAsync();     // flush what is left
        await base.StopAsync(cancellationToken);
    }

    private async Task WriteBatchAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken cancellationToken) =>
        await context.WriteBatchAsync(batch, timeProvider.GetUtcNow(), cancellationToken);
}
```

```csharp
// LOGIC — buffering and flush policy, no I/O at all.
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

    public async Task FlushAsync(CancellationToken cancellationToken = default) { /* same shape */ }

    private async Task FlushBatchAsync(IReadOnlyList<T> batch, CancellationToken cancellationToken)
    {
        // The batch already left the buffer: if the flush fails it is lost, so log + count
        // BEFORE rethrowing — the timer callback has nobody else to report to.
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

`BatchAccumulator<T>` is the highest-value class in the kit: a hard-to-get-right concurrency policy,
tested in unit tests with a fake `flushAsync` and a fake `TimeProvider`.

## Non-negotiable corollaries

- **Ports in `Application.UseCases/IServices/`, implementations in `Api/Services/`.** The handler
  depends on the interface; the SDK never appears in a layer below `Api`.
- **The port is the seam the tests use.** One interface per integration, narrow, with only the
  operations the handlers actually call.
- **The pure class returns domain types**, never SDK types and never persistence types. That is what
  makes it reusable by the tests without the package.

## Anti-patterns

- `if (response.State == "IN_TRANSIT")` inside the SDK client class.
- A handler that both queries SQL *and* decides what a 409 means.
- A "logic" class that takes an `HttpClient`, a `DbContext` or a `QueueClient` in its constructor.
- A mapper that returns the vendor DTO and lets the endpoint decide the status code.
- Testing only through a real database because "the pure part is trivially correct" — it is not, and
  the pure part is where the cost of a bug is.
- Making the pure class `internal` and adding `InternalsVisibleTo` instead of just making it `public`.
