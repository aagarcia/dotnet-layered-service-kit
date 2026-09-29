# 08 — Ingesta asíncrona: validar, normalizar, encolar, 202

## El patrón

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

`202` significa **aceptado para procesar, no terminado**. El contrato del endpoint con el llamador es
«tengo tu pedido y está a salvo en una cola», no «tu pedido está procesado». Devolver `201` con una
URL de recurso, o `200`, es una mentira sobre un estado que el servicio no puede observar.

El correlation id es `Guid.CreateVersion7()`: ordenable por fecha de creación, así que una cola
ordenada de ids te da el orden de llegada gratis y la base de datos puede usarlo como clave agrupada
sin fragmentación. Se genera en el handler, no en el filtro, porque pertenece a la correlación de
negocio, no a la petición HTTP.

## Normalizar antes de encolar

El mensaje en la cola debe tener la forma **normalizada**, no la que llegó por el cable.

Por qué: el consumidor no debería tener que conocer el formato del sistema de origen, sus unidades,
su formato de fecha ni sus nombres de campo. Un mensaje en bruto en la cola significa que cada
consumidor reimplementa la misma normalización, y en cuanto el sistema de origen cambia de formato,
todos los consumidores se rompen a la vez. Normalizar una sola vez, en el límite, significa que los
cambios del sistema de origen los absorbe un único archivo.

Corolario: la misma decisión tomada en el límite de la API y más tarde en el consumidor es trabajo
duplicado. Muévela; no dejes las dos.

## El contrato de la cola tiene versión y está documentado

```csharp
namespace Contoso.Orders.Api.Services;

/// <summary>
/// Mensaje escrito en la cola de pedidos. CONTRATO v1 — cualquier cambio incompatible en el payload
/// exige un nombre de cola nuevo (orders-v2) o un campo aditivo explícito, más una actualización de
/// docs/API-CONTRACT.md y del consumidor.
/// </summary>
public sealed record OrderMessage(Guid CorrelationId, NormalizedOrder Payload);
```

Si el transporte no tiene huecos de metadatos, incrusta el correlation id en el sobre — el consumidor
lo necesita para volver a unir sus filas con el registro de auditoría.

Documenta en `docs/API-CONTRACT.md`: nombre de la cola, esquema del mensaje, regla de versionado,
throughput esperado y qué debe tolerar el consumidor (entrega duplicada, desorden, reintento). Da por
hecho que el consumidor es un desplegable independiente que no controlas.

## El cliente de la cola

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

    // CreateIfNotExistsAsync por petición era un viaje de ida y vuelta extra en cada llamada, y el
    // throttling que provocaba se notaba bajo carga. Un intento por proceso, detrás de un lock.
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

Registra el `QueueService` como **singleton**. Contiene un `QueueClient` y un lock; las instancias
por petición recrean el cliente y pierden el flag de «ya creado», así que vuelves al viaje de ida y
vuelta por petición. Mejor aún: crea la cola en un `IHostedService` al arrancar y deja la ruta de
envío sin dependencias.

## Logging de auditoría sin bloqueo

La auditoría best-effort nunca debe añadir latencia, un modo de fallo ni una excepción al flujo de
negocio. Eso significa un canal acotado, un escritor `BackgroundService` que sea dueño del trabajo
contra la base de datos y un contador por cada pérdida.

```csharp
// Registro — Singleton, y esto no es opcional.
services.AddSingleton(Channel.CreateBounded<AuditLogEntry>(new BoundedChannelOptions(10_000)
{
    FullMode = BoundedChannelFullMode.Wait,   // NO DropWrite — ver abajo
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
            // FullMode.Wait + TryWrite significa: lleno -> devuelve false sin bloquear.
            // Esa es la única forma de notar la pérdida y contarla.
            OrderTelemetry.AuditErrors.Add(1, new KeyValuePair<string, object?>("reason", "channel_full"));
        }
    }
}
```

Dos errores que este diseño existe para evitar, ambos pagados en producción:

1. **`BoundedChannelFullMode.DropWrite`.** `TryWrite` devuelve `true` *aunque haya descartado el
   elemento*. La pérdida se vuelve completamente invisible: sin excepción, sin log, sin contador. Te
   enteras por un cliente. Con `Wait`, `TryWrite` devuelve `false` sin bloquear — la pérdida es
   observable.
2. **Vida `Scoped` en el wrapper.** Un servicio `Scoped` que envuelve un canal `Singleton` significa
   que cada petición recibe su propia instancia y el escritor nunca ve los datos. `Singleton`, y lo
   que lo consuma tiene que ser compatible con `Singleton`.

Explicación completa de ambos en `references/14-production-pitfalls.md`.

## Rechazos que nunca llegan al handler

Las peticiones rechazadas por autenticación, autorización o rate limiting nunca ejecutan el handler,
así que nunca producen una entrada de auditoría. Eso es correcto e intencionado — esos rechazos son
la respuesta del framework, y fabricar una fila de auditoría por cada uno convierte una avalancha en
un ataque de amplificación de escrituras. Audita lo que el handler vio de verdad.

## Anti-patrones

- `200` o `201` desde un endpoint cuyo trabajo solo se encola.
- El DTO en bruto del sistema de origen en la cola, dejando la normalización al consumidor.
- `CreateIfNotExistsAsync` dentro de la ruta de envío.
- Un `QueueClient` creado por petición, o `QueueService` registrado como Scoped.
- Un `Guid.NewGuid()` por petición para el correlation id en vez de `Guid.CreateVersion7()`.
- Un contrato de cola sin versión, o un cambio incompatible del payload sin nombre de cola nuevo.
- `Channel<T>.DropWrite` en cualquier sitio donde quieras contar pérdidas.
- `await channel.Writer.WriteAsync(...)` en la ruta de la petición — eso hace que la petición espere
  al consumidor de auditoría y convierte una dependencia best-effort en una dependencia dura.
- Un escritor de auditoría cuyas excepciones escapan de `ExecuteAsync` — desde .NET 6 eso detiene el
  host, así que un solo flush malo en un ayudante best-effort tumba toda la API.
- Un `BackgroundService` de auditoría que pierde su buffer al apagarse — haz flush en `StopAsync`.
- Registrar cada ingesta en Information; un endpoint de ingesta de alto volumen llena la factura del
  log y esconde los errores.
