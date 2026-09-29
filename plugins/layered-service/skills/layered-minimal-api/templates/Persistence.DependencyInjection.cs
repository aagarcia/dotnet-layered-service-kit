// The single registration point of the persistence layer. Nothing here may reference the layers
// above. See references/04-persistence-efcore.md.
//
// This project is a class library, so it starts with no framework references. It needs:
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

        // A factory, not AddDbContext: a BackgroundService that opens its own context needs one,
        // and with the factory everywhere no path can accidentally reuse a context concurrently.
        services.AddDbContextFactory<OrdersDbContext>((sp, options) =>
        {
            var sql = sp.GetRequiredService<IOptions<SqlOptions>>().Value;
            options.UseSqlServer(sql.ConnectionString, b => b.EnableRetryOnFailure());
        });

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IOrdersContext, OrdersContext>();

        // Best-effort audit: bounded channel + writer. Both Singleton - a Scoped wrapper around a
        // shared channel gives every request its own instance and the writer never sees the data.
        services.AddSingleton(Channel.CreateBounded<AuditLogEntry>(new BoundedChannelOptions(10_000)
        {
            // Wait, NOT DropWrite: under DropWrite, TryWrite returns true even when it discards,
            // making the loss invisible. With Wait, TryWrite returns false without blocking, and
            // the drop can be detected and counted.
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
