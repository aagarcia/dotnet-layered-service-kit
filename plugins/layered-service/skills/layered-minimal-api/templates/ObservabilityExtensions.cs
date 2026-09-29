// OpenTelemetry with the Azure Monitor exporter: traces + metrics + logs in one call, with
// ILogger<T> captured as-is. No Serilog, no classic Application Insights SDK.
//
// See references/10-observability.md.
using Contoso.Orders.Domain.Telemetry;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Contoso.Orders.Api.Observability;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ApplicationInsights:ConnectionString"];

        // No connection string: start anyway and export nothing. A missing setting must not take
        // the service down, and it must not be the reason a laptop cannot run it.
        if (string.IsNullOrEmpty(connectionString))
        {
            return services;
        }

        services.AddOpenTelemetry()
            // UseAzureMonitor already instruments ASP.NET Core and HttpClient.
            .UseAzureMonitor(options => options.ConnectionString = connectionString)
            .WithTracing(tracing => tracing
                // Without AddSource, our spans are never exported.
                .AddSource(OrderTelemetry.SourceName)
                // Without SqlClient instrumentation, database time is invisible in the trace.
                .AddSqlClientInstrumentation())
            .WithMetrics(metrics => metrics
                // Without AddMeter, our counters and histograms are never exported.
                .AddMeter(OrderTelemetry.SourceName)
                .AddRuntimeInstrumentation());

        return services;
    }
}
