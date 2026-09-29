// OpenTelemetry con el exporter de Azure Monitor: trazas + métricas + logs en una sola llamada, con
// ILogger<T> capturado tal cual. Sin Serilog, sin el SDK clásico de Application Insights.
//
// Ver references/10-observability.md.
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

        // Sin connection string: arranca igual y no exporta nada. Un ajuste faltante no debe tirar
        // el service abajo, ni puede ser la razón por la que un portátil no lo puede ejecutar.
        if (string.IsNullOrEmpty(connectionString))
        {
            return services;
        }

        services.AddOpenTelemetry()
            // UseAzureMonitor ya instrumenta ASP.NET Core y HttpClient.
            .UseAzureMonitor(options => options.ConnectionString = connectionString)
            .WithTracing(tracing => tracing
                // Sin AddSource, nuestros spans nunca se exportan.
                .AddSource(OrderTelemetry.SourceName)
                // Sin la instrumentación de SqlClient, el tiempo de base de datos es invisible en la traza.
                .AddSqlClientInstrumentation())
            .WithMetrics(metrics => metrics
                // Sin AddMeter, nuestros contadores e histogramas nunca se exportan.
                .AddMeter(OrderTelemetry.SourceName)
                .AddRuntimeInstrumentation());

        return services;
    }
}
