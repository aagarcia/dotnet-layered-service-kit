# 03 — Domain

## The rule

`Domain` is plain C#: models, enums, and telemetry instruments. No EF attributes, no packages, no
`ProjectReference`, no validation, no methods with logic.

Why: the domain is the vocabulary the whole solution speaks. If it can drift at runtime, so can every
other layer. Attributes on a domain model mean the persistence mapping is decided in two places at once,
and the "pure" layer stops being pure.

## Entities — mapped, not attributed

```csharp
namespace Contoso.Orders.Domain.Entities;

public class Order
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public OrderState State { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public ICollection<OrderLine> Lines { get; set; } = [];
}
```

No `[Table]`, no `[Column]`, no `[Key]`. All of that lives in
`Infrastructure.Persistence/Configurations/<Module>/`.

## Models — normalized shapes and pure inputs

Models are not tables. They are the shapes that travel between layers and between processes.

```csharp
namespace Contoso.Orders.Domain.Models;

/// <summary>Exactly what the queue carries. Versioned contract — see docs/API-CONTRACT.md.</summary>
public sealed record NormalizedOrder(
    Guid CorrelationId,
    string Reference,
    IReadOnlyList<NormalizedLine> Lines,
    DateTimeOffset NormalizedAt);

public sealed record NormalizedLine(string Sku, int Quantity, decimal UnitPrice);
```

```csharp
namespace Contoso.Orders.Domain.Models;

public sealed record CarrierShipment(string Code, string State, DateTimeOffset UpdatedAt, string? CarrierName);
```

Why `record` for a cross-process contract: value equality makes assertions trivial, immutability means a
consumer cannot corrupt a message another handler is still reading, and the positional form keeps the
contract visible in one line.

## Enums

```csharp
namespace Contoso.Orders.Domain.Enums;

public enum OrderState
{
    Unknown = 0,
    Received = 1,
    InTransit = 2,
    Delivered = 3,
    Cancelled = 4,
}
```

Rules:

- `Unknown = 0` is the **default**, so an uninitialized enum never silently claims a real state.
- Never reorder or reuse a numeric value. These numbers end up in queues, databases and telemetry.
- Persist enums as `string` via `HasConversion<string>()` unless you have a reason not to: a renumbering
  then cannot corrupt stored data.

## Value objects as plain records

```csharp
namespace Contoso.Orders.Domain.Models;

public sealed record OrderReference(string Value)
{
    public static OrderReference Parse(string raw) =>
        new(raw.Trim().ToUpperInvariant());
}
```

Keep them plain. A value object that validates in its constructor forces every deserializer to go
through a factory, which is friction with no payoff at this scale. Validate at the boundary
(`ValidationFilter<T>`), not in the model.

## Telemetry instruments live here

`Domain` has no packages, and `System.Diagnostics` is in the BCL, so the `Meter`/`ActivitySource`
definition has no home other than here.

```csharp
namespace Contoso.Orders.Domain.Telemetry;

/// <summary>
/// Single source of instrumentation for this service. The Api layer registers SourceName
/// explicitly (AddSource/AddMeter) so these spans and metrics are exported alongside the
/// automatic ASP.NET Core and SqlClient ones.
/// </summary>
public static class OrderTelemetry
{
    public const string SourceName = "Contoso.Orders";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> OrdersIngested = Meter.CreateCounter<long>(
        "orders.ingested",
        unit: "{order}",
        description: "Orders accepted by the ingestion endpoint, by result and source.");

    public static readonly Counter<long> OrdersDropped = Meter.CreateCounter<long>(
        "orders.dropped",
        unit: "{order}",
        description: "Orders dropped before enqueuing, by reason.");

    public static readonly Counter<long> AuditErrors = Meter.CreateCounter<long>(
        "orders.audit.errors",
        unit: "{event}",
        description: "Audit log entries that could not be persisted, by reason. Best effort: never affects the main flow.");

    public static readonly Histogram<double> IngestDuration = Meter.CreateHistogram<double>(
        "orders.ingest.duration",
        unit: "ms",
        description: "End-to-end duration of the ingestion handler.");
}
```

Rules:

- `SourceName` is a **constant**, one per service. It is the name the Api layer registers.
- Instrument names are `noun.verb` in ASCII with dots, never localized, never rebuilt per call.
- Every counter that can lose something takes a `reason` tag. A counter without a reason tag cannot
  answer "why".
- The `Meter`/`ActivitySource` are `static readonly` — creating one per request leaks instruments.

## Anti-patterns

- `[Table]`/`[Column]`/`[Key]` attributes on a domain entity.
- A `Domain` method that calls `DateTime.Now` or reads `IConfiguration`. The domain is handed its
  inputs; it does not go get them.
- A domain model that imports `Microsoft.EntityFrameworkCore` "just for the attributes".
- An enum with no `Unknown = 0`, or whose members are renumbered later.
- Telemetry instruments declared in the `Api` layer and referenced from a handler — the handler is in
  `Application.UseCases`, which must not see the SDK-level wiring.
- A `Meter` created inside a constructor.
- Time zone logic in a domain model instead of a shared `Utils` helper with a fixed offset.
