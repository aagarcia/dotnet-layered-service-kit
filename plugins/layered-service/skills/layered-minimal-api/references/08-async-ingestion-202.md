# 08 — Async ingestion: validate, normalize, enqueue, 202

## The pattern

```
client ──POST /orders/ingest──▶ JWT (401) ─▶ client-id policy (403)
                                 ─▶ rate limiter (429)
                                 ─▶ ValidationFilter<ReceivedOrder> (400)
                                 ─▶ OrderHandler
                                      normalize (pure, no I/O)
                                      enqueue  (the only I/O)
                                      audit    (non-blocking, best effort)
                                 ◀── 202 Accepted { correlationId }
```

`202` means **accepted for processing, not done**. The endpoint's contract with the caller is "I have
your order and it is safely in a queue", not "your order is processed". Returning `201` with a
resource URL, or `200`, is a lie about a state the service cannot observe.

The correlation id is `Guid.CreateVersion7()`: sortable by creation time, so an ordered queue of ids
tells you the arrival order for free and the database can use it as a clustered key without
fragmentation. It is generated in the handler, not the filter, because it belongs to the business
correlation, not to the HTTP request.

## Normalize before enqueuing

The message on the queue must be the **normalized** shape, not what arrived on the wire.

Why: the consumer should not have to know the upstream's format, its units, its date format or its
field names. A raw message on the queue means every consumer re-implements the same normalization, and
the moment the upstream changes its format, every consumer breaks at once. Normalizing once, at the
boundary, means the upstream's changes are absorbed by one file.

Corollary: the same decision made at the boundary in the API and later in the consumer is duplicated
work. Move it; do not leave both.

## The queue contract is versioned and documented

```csharp
namespace Contoso.Orders.Api.Services;

/// <summary>
/// Message written to the orders queue. CONTRACT v1 — any breaking change to the payload
/// requires a new queue name (orders-v2) or an explicit additive field, plus an update to
/// docs/API-CONTRACT.md and to the consumer.
/// </summary>
public sealed record OrderMessage(Guid CorrelationId, NormalizedOrder Payload);
```

If the transport has no metadata slots, embed the correlation id in the envelope — the consumer needs
it to join its own rows back to the audit trail.

Document in `docs/API-CONTRACT.md`: queue name, message schema, versioning rule, expected throughput,
and what the consumer must tolerate (duplicate delivery, out-of-order, retry). Assume the consumer is
a separate deployable you do not control.

## The queue client

```csharp
public sealed class QueueService(IOptions<QueueOptions> options) : IQueueService
{
    private readonly QueueClient _ordersQueue =
        new(options.Value.ConnectionString, options.Value.OrdersQueueName);
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private volatile bool _created;

    public async Task EnqueueOrderAsync(OrderMessage message, CancellationToken cancellationToken)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        await _ordersQueue.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);
    }

    // CreateIfNotExistsAsync per request was an extra round trip on every single call, and the
    // throttling that caused was visible under load. One attempt per process, behind a lock.
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

Register the `QueueService` as a **singleton**. It holds one `QueueClient` and one lock; per-request
instances re-create the client and lose the "already created" flag, so you are back to the round trip
per request. Better still, create the queue in an `IHostedService` at startup and make the send path
pure.

## Non-blocking audit logging

Best-effort auditing must never add latency, a failure mode or a thrown exception to the business
flow. That means a bounded channel, a `BackgroundService` writer that owns the database work, and a
counter for every drop.

```csharp
// Registration — Singleton, and this is not optional.
services.AddSingleton(Channel.CreateBounded<AuditLogEntry>(new BoundedChannelOptions(10_000)
{
    FullMode = BoundedChannelFullMode.Wait,   // NOT DropWrite — see below
    SingleReader = true,
    SingleWriter = false,
}));
services.AddSingleton<IAuditLogService, AuditLogService>();
services.AddHostedService<AuditBatchWriter>();
```

```csharp
public sealed class AuditLogService(Channel<AuditLogEntry> channel) : IAuditLogService
{
    public void TryEnqueue(AuditLogEntry entry)
    {
        if (!channel.Writer.TryWrite(entry))
        {
            // FullMode.Wait + TryWrite means: full -> return false without blocking.
            // That is the only way to notice the drop and count it.
            OrderTelemetry.AuditErrors.Add(1, new KeyValuePair<string, object?>("reason", "channel_full"));
        }
    }
}
```

Two mistakes this design exists to prevent, both paid for in production:

1. **`BoundedChannelFullMode.DropWrite`.** `TryWrite` returns `true` *even when it discarded the item*.
   The loss becomes completely invisible: no exception, no log, no counter. You find out from a
   customer. With `Wait`, `TryWrite` returns `false` without blocking — the drop is observable.
2. **Scoped lifetime on the wrapper.** A `Scoped` service wrapping a `Singleton` channel means every
   request gets its own instance, and the writer never sees the data. `Singleton`, and whatever
   consumes it must be compatible with `Singleton`.

Full write-up of both in `references/14-production-pitfalls.md`.

## Rejections that never reach the handler

Requests rejected by authentication, authorization or rate limiting never execute the handler, so they
never produce an audit entry. That is correct and intentional — those rejections are the framework's
answer, and manufacturing an audit row for each one means a flood becomes a write amplification
attack. Audit what the handler actually saw.

## Anti-patterns

- `200` or `201` from an endpoint whose work is only enqueued.
- The raw upstream DTO on the queue, normalizing left to the consumer.
- `CreateIfNotExistsAsync` inside the send path.
- A `QueueClient` created per request, or `QueueService` registered Scoped.
- A per-request `Guid.NewGuid()` for the correlation id instead of `Guid.CreateVersion7()`.
- An unversioned queue contract, or a breaking payload change with no new queue name.
- `Channel<T>.DropWrite` anywhere you intend to count drops.
- `await channel.Writer.WriteAsync(...)` in the request path — that makes the request wait for the
  audit consumer and turns a best-effort dependency into a hard one.
- An audit writer whose exceptions escape `ExecuteAsync` — since .NET 6 that stops the host, so one bad
  flush in a best-effort helper takes down the whole API.
- An audit `BackgroundService` that loses its buffer on shutdown — flush in `StopAsync`.
- Logging every ingestion at Information; a high-volume ingest endpoint fills the log bill and hides
  the errors.
