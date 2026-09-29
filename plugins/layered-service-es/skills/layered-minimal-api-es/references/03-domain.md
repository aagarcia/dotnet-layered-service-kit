# 03 — Dominio

## La regla

`Domain` es C# puro: modelos, enums e instrumentos de telemetría. Sin atributos de EF, sin paquetes,
sin `ProjectReference`, sin validación, sin métodos con lógica.

Por qué: el dominio es el vocabulario que habla toda la solución. Si puede derivar en runtime, todas
las demás capas también pueden. Los atributos en un modelo de dominio significan que el mapeo de
persistencia se decide en dos lugares a la vez, y la capa «pura» deja de ser pura.

## Entidades — mapeadas, no atribuidas

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

Sin `[Table]`, sin `[Column]`, sin `[Key]`. Todo eso vive en
`Infrastructure.Persistence/Configurations/<Module>/`.

## Modelos — formas normalizadas y entradas puras

Los modelos no son tablas. Son las formas que viajan entre capas y entre procesos.

```csharp
namespace Contoso.Orders.Domain.Models;

/// <summary>Exactamente lo que transporta la cola. Contrato versionado — ver docs/API-CONTRACT.md.</summary>
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

Por qué `record` para un contrato entre procesos: la igualdad por valor hace triviales las aserciones,
la inmutabilidad impide que un consumidor corrompa un mensaje que otro handler todavía está leyendo, y
la forma posicional mantiene el contrato visible en una línea.

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

Reglas:

- `Unknown = 0` es el **valor por defecto**, así que un enum sin inicializar nunca afirma en silencio un
  estado real.
- Nunca reordenar ni reutilizar un valor numérico. Estos números terminan en colas, bases de datos y
  telemetría.
- Persistir enums como `string` con `HasConversion<string>()` salvo que tengas una razón para no
  hacerlo: una renumeración entonces no puede corromper los datos almacenados.

## Objetos de valor como records planos

```csharp
namespace Contoso.Orders.Domain.Models;

public sealed record OrderReference(string Value)
{
    public static OrderReference Parse(string raw) =>
        new(raw.Trim().ToUpperInvariant());
}
```

Mantenlos simples. Un objeto de valor que valida en su constructor obliga a que cada deserializador
pase por una factory, lo que es fricción sin contrapartida a esta escala. Valida en la frontera
(`ValidationFilter<T>`), no en el modelo.

## Los instrumentos de telemetría viven aquí

`Domain` no tiene paquetes y `System.Diagnostics` está en el BCL, así que la definición del
`Meter`/`ActivitySource` no tiene otro hogar que este.

```csharp
namespace Contoso.Orders.Domain.Telemetry;

/// <summary>
/// Única fuente de instrumentación para este servicio. La capa Api registra SourceName
/// explícitamente (AddSource/AddMeter) para que estos spans y métricas se exporten junto con
/// los automáticos de ASP.NET Core y SqlClient.
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

Reglas:

- `SourceName` es una **constante**, una por servicio. Es el nombre que registra la capa Api.
- Los nombres de instrumento son `noun.verb` en ASCII con puntos, nunca localizados, nunca
  reconstruidos en cada llamada.
- Cada contador que puede perder algo lleva un tag `reason`. Un contador sin tag de reason no puede
  responder «por qué».
- El `Meter`/`ActivitySource` es `static readonly` — crear uno por request filtra instrumentos.

## Anti-patrones

- Atributos `[Table]`/`[Column]`/`[Key]` en una entidad de dominio.
- Un método de `Domain` que llame a `DateTime.Now` o lea `IConfiguration`. Al dominio le pasan sus
  entradas; no va a buscarlas.
- Un modelo de dominio que importa `Microsoft.EntityFrameworkCore` «solo por los atributos».
- Un enum sin `Unknown = 0`, o cuyos miembros se renumeran más tarde.
- Instrumentos de telemetría declarados en la capa `Api` y referenciados desde un handler — el handler
  está en `Application.UseCases`, que no debe ver el cableado de nivel SDK.
- Un `Meter` creado dentro de un constructor.
- Lógica de zona horaria en un modelo de dominio en vez de un helper compartido en `Utils` con un
  offset fijo.
