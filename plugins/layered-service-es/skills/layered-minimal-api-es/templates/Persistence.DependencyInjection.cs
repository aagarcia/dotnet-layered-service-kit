// El único punto de registro de la capa de persistencia. Nada aquí puede referenciar las capas de
// arriba. Lee references/04-persistence-efcore.md.
//
// Este proyecto es una librería de clases, así que no arranca con ninguna referencia de framework.
// Necesita:
//   Microsoft.EntityFrameworkCore.SqlServer              UseSqlServer / EnableRetryOnFailure
//   Microsoft.Extensions.Hosting.Abstractions            BackgroundService / AddHostedService
//   Microsoft.Extensions.Options.ConfigurationExtensions  Configure<T>(IConfiguration)
using System.Threading.Channels;
using Contoso.Orders.Domain.Models;
using Contoso.Orders.Infrastructure.Persistence.Contexts.Audit;
using Contoso.Orders.Infrastructure.Persistence.Contexts.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Contoso.Orders.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SqlOptions>(configuration.GetSection(SqlOptions.SectionName));

        // Una factory, no AddDbContext: un BackgroundService que abre su propio contexto necesita
        // una, y con la factory en todas partes ningún camino puede reutilizar por accidente un
        // contexto de forma concurrente.
        services.AddDbContextFactory<OrdersDbContext>((sp, options) =>
        {
            var sql = sp.GetRequiredService<IOptions<SqlOptions>>().Value;
            options.UseSqlServer(sql.ConnectionString, b => b.EnableRetryOnFailure());
        });

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IOrdersContext, OrdersContext>();

        // Auditoría de mejor esfuerzo: canal acotado + writer. Ambos Singleton: un wrapper Scoped
        // alrededor de un canal compartido le da a cada request su propia instancia y el writer nunca
        // ve los datos.
        services.AddSingleton(Channel.CreateBounded<AuditLogEntry>(new BoundedChannelOptions(10_000)
        {
            // Wait, NO DropWrite: con DropWrite, TryWrite devuelve true incluso cuando descarta,
            // lo que hace invisible la pérdida. Con Wait, TryWrite devuelve false sin bloquear, y
            // la caída se puede detectar y contar.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        }));

        services.AddSingleton<IAuditLogContext, AuditLogContext>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddHostedService<AuditBatchWriter>();

        return services;
    }
}
