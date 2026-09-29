# 10 — Observabilidad

## La regla

OpenTelemetry, exportado con el exportador de Azure Monitor. **Nada de Serilog**, nada del SDK
clásico de Application Insights, ningún sink de logging de terceros. Los nombres de los instrumentos
son constantes en `Domain/Telemetry/<Service>Telemetry`. Registra la telemetría solo si existe una
cadena de conexión.

Por qué: el SDK clásico más un sink de logging son dos piezas móviles más que producen las mismas
tres señales. La distro configura trazas, métricas y logs en una sola llamada, y `ILogger<T>` — que
tu código ya usa — se captura sin ningún cambio de código.

## Registro

```csharp
namespace Contoso.Orders.Api.Observability;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ApplicationInsights:ConnectionString"];

        // Sin cadena de conexión: arranca igualmente, solo que sin exportar nada. Un ajuste que
        // falta no puede tumbar el servicio, ni puede ser el motivo de que un portátil no pueda
        // ejecutarlo.
        if (string.IsNullOrEmpty(connectionString)) return services;

        services.AddOpenTelemetry()
            .UseAzureMonitor(options => options.ConnectionString = connectionString)
            .WithTracing(tracing => tracing
                .AddSource(OrderTelemetry.SourceName)      // nuestro ActivitySource
                .AddSqlClientInstrumentation())            // queries de EF Core, correlacionadas con la petición
            .WithMetrics(metrics => metrics
                .AddMeter(OrderTelemetry.SourceName)        // nuestro Meter
                .AddRuntimeInstrumentation());              // GC, thread pool, excepciones

        return services;
    }
}
```

Lo que la distro ya hace por ti: la instrumentación de peticiones de ASP.NET Core y la instrumentación
de `HttpClient` saliente. Lo que debes añadir tú: **tu propio source/meter** (si no, tus spans y tus
métricas nunca se exportan) y la **instrumentación del cliente SQL** (si no, el tiempo de base de
datos es invisible en la traza).

Llama a `AddObservability` lo primero en `Program.cs`, así todo lo que se registre después queda
instrumentado.

## Instrumentos

Definidos una sola vez en `Domain/Telemetry` — ver `references/03-domain.md`. Reglas:

- Un `ActivitySource` y un `Meter` por servicio, ambos con nombre derivado de una única constante
  `SourceName`.
- Los nombres de instrumento son `noun.verb` en ASCII (`orders.ingested`), nunca localizados, nunca
  construidos en tiempo de ejecución.
- Todo contador que pueda perder algo lleva un tag `reason`. Un contador sin motivo no puede responder
  a «por qué».
- Histogramas para duraciones, contadores para eventos. Gauges up-down para conteos en vivo.
- Cardinalidad acotada en los tags: nada de id de pedido, nada de código de referencia, nada de ruta
  completa de URL con un id dentro. Los atributos de alta cardinalidad son la forma más rápida de
  reventar la factura de telemetría.

## Cómo usarlos

```csharp
using var activity = OrderTelemetry.ActivitySource.StartActivity("OrderHandler.Handle");
activity?.SetTag("orders.correlation_id", correlationId);

var startedAt = timeProvider.GetTimestamp();      // TimeProvider, no Stopwatch: testeable
// ... hacer el trabajo ...
OrderTelemetry.IngestDuration.Record(timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);

OrderTelemetry.OrdersIngested.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
OrderTelemetry.OrdersDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

if (outcome != "accepted")
    activity?.SetStatus(ActivityStatusCode.Error, outcome);
```

Toma las duraciones del `TimeProvider` inyectado, nunca de `DateTime.Now` ni de `Stopwatch`
directamente — eso es lo que permite verificar el tiempo de un handler en una prueba unitaria con un
reloj falso.

Usa siempre `using` con la activity: olvidarlo filtra el span y la traza de la petición se convierte
en un bosque de huérfanos.

Marca el estado `Error` cuando el resultado sea realmente un fallo, y registra el detalle por separado.
Un span marcado como `Error` sin log no es accionable; un log con un span `Error` y sin detalle es
peor.

## Health checks

```csharp
builder.Services.AddHealthChecks();

// Deliberadamente sin dependencias.
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder builder) =>
        builder.MapHealthChecks("/health").AllowAnonymous();
}
```

Por qué no hay comprobaciones de dependencias en la sonda de liveness: una sonda de liveness responde
«¿debo reiniciar este proceso?». Si comprueba la base de datos y la base de datos se cae brevemente,
el orquestador reinicia un proceso sano y convierte la caída de una dependencia en una caída general.
Si necesitas readiness, exponla como un endpoint **separado**.

`/health` es `.AllowAnonymous()` y está excluido de las métricas de peticiones, o el tráfico de las
sondas domina las métricas.

## Anti-patrones

- Serilog, `Serilog.Sinks.*`, o `ILogger` de una librería de logging de terceros.
- El SDK clásico de `ApplicationInsights` junto a la distro.
- `AddOpenTelemetry()` sin la protección de `ConnectionString`, de modo que un ajuste que falta rompe
  el arranque.
- Declarar el `Meter`/`ActivitySource` en la capa `Api` mientras los handlers de
  `Application.UseCases` los referencian.
- Olvidar `AddSource`/`AddMeter`: el código compila, las métricas nunca se exportan y nadie se
  entera durante semanas.
- `using` olvidado en una activity, lo que filtra spans.
- Tags de alta cardinalidad (ids, referencias, rutas completas) en las métricas.
- Un contador sin tag `reason`.
- Registrar cada ingesta exitosa en Information.
- Health checks que fallan por una dependencia, conectados a la sonda de liveness.
- `/health` contado en las métricas de peticiones.
- Un correlation id de traza distribuida que no es el correlation id de negocio, de modo que la traza
  y el registro de auditoría no se pueden unir.
