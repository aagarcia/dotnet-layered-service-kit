# 10 — Observability

## The rule

OpenTelemetry, exported with the Azure Monitor exporter. **No Serilog**, no classic Application
Insights SDK, no third-party logging sink. Instrument names are constants in
`Domain/Telemetry/<Service>Telemetry`. Register telemetry only if a connection string exists.

Why: the classic SDK plus a logging sink is two more moving parts that produce the same three signals.
The distro configures traces, metrics and logs in one call, and `ILogger<T>` — which your code already
uses — is captured without any code change.

## Registration

```csharp
namespace Contoso.Orders.Api.Observability;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ApplicationInsights:ConnectionString"];

        // No connection string: still start, just export nothing. A missing setting must not
        // take the service down, and it must not be the reason a laptop cannot run it.
        if (string.IsNullOrEmpty(connectionString)) return services;

        services.AddOpenTelemetry()
            .UseAzureMonitor(options => options.ConnectionString = connectionString)
            .WithTracing(tracing => tracing
                .AddSource(OrderTelemetry.SourceName)      // our ActivitySource
                .AddSqlClientInstrumentation())            // EF Core queries, correlated with the request
            .WithMetrics(metrics => metrics
                .AddMeter(OrderTelemetry.SourceName)        // our Meter
                .AddRuntimeInstrumentation());              // GC, thread pool, exceptions

        return services;
    }
}
```

What the distro already does for you: ASP.NET Core request instrumentation and outgoing `HttpClient`
instrumentation. What you must add: **your own source/meter** (otherwise your spans and metrics are
never exported) and **SQL client instrumentation** (otherwise database time is invisible in the trace).

Call `AddObservability` first in `Program.cs`, so everything registered after it is instrumented.

## Instruments

Defined once in `Domain/Telemetry` — see `references/03-domain.md`. Rules:

- One `ActivitySource` and one `Meter` per service, both named from a single `SourceName` constant.
- Instrument names are ASCII `noun.verb` (`orders.ingested`), never localized, never built at runtime.
- Every counter that can lose something carries a `reason` tag. A counter without a reason cannot
  answer "why".
- Histograms for durations, counters for events. Up-down gauges for live counts.
- A bounded cardinality on tags: no order id, no reference code, no full URL path with an id in it.
  High-cardinality attributes are the fastest way to blow a telemetry bill.

## Using them

```csharp
using var activity = OrderTelemetry.ActivitySource.StartActivity("OrderHandler.Handle");
activity?.SetTag("orders.correlation_id", correlationId);

var startedAt = timeProvider.GetTimestamp();      // TimeProvider, not Stopwatch: testable
// ... do the work ...
OrderTelemetry.IngestDuration.Record(timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);

OrderTelemetry.OrdersIngested.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
OrderTelemetry.OrdersDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

if (outcome != "accepted")
    activity?.SetStatus(ActivityStatusCode.Error, outcome);
```

Take durations from the injected `TimeProvider`, never from `DateTime.Now` or `Stopwatch` directly —
that is what makes a handler's timing assertable in a unit test with a fake clock.

Always `using` the activity: forgetting it leaks the span and the request trace becomes a forest of
orphans.

Set `Error` status when the outcome is genuinely a failure, and log the detail separately. A span
marked `Error` with no log is unactionable; a log with an `Error` span and no detail is worse.

## Health checks

```csharp
builder.Services.AddHealthChecks();

// Deliberately dependency-free.
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder builder) =>
        builder.MapHealthChecks("/health").AllowAnonymous();
}
```

Why no dependency checks in the liveness probe: a liveness probe answers "should this process be
restarted". If it checks the database and the database is briefly down, the orchestrator restarts a
healthy process and turns a dependency outage into an outage. If you need readiness, expose it as a
**separate** endpoint.

`/health` is `.AllowAnonymous()` and excluded from request metrics, or the probe traffic dominates the
metrics.

## Anti-patterns

- Serilog, `Serilog.Sinks.*`, or `ILogger` from a third-party logging library.
- The classic `ApplicationInsights` SDK alongside the distro.
- `AddOpenTelemetry()` with no `ConnectionString` guard, so a missing setting crashes startup.
- Declaring the `Meter`/`ActivitySource` in the `Api` layer while handlers in
  `Application.UseCases` reference them.
- Forgetting `AddSource`/`AddMeter`: the code compiles, the metrics are never exported, and nobody
  notices for weeks.
- `using` forgotten on an activity, leaking spans.
- High-cardinality tags (ids, references, full paths) in metrics.
- A counter with no `reason` tag.
- Logging every successful ingestion at Information.
- Health checks that fail on a dependency, wired into the liveness probe.
- `/health` counted in the request metrics.
- A distributed-trace correlation id that is not the business correlation id, so the trace and the
  audit trail cannot be joined.
