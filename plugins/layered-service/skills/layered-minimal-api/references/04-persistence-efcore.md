# 04 — Persistence with EF Core

## The rule

One `DbContext`. One `IEntityTypeConfiguration<T>` per entity in `Configurations/<Module>/`. One
`Context` per module — **never a generic `IRepository<T>`**. All registration in
`AddPersistence(services, configuration)`.

Why no generic repository: a generic `IRepository<T>` promises every entity can be queried and saved
anywhere, which is false for most of them, and it hides the actual query — you cannot read the SQL
without stepping into the repository. A module `Context` is a hand-written list of the operations the
module actually needs, and each one is a query you can review.

**Persistence is optional.** A pure receiver that validates, normalizes and enqueues needs no database
at all. If the only reason for a `DbContext` is an audit table, say so in `CLAUDE.md` rather than
pretending the domain is relational.

## DbContext

```csharp
namespace Contoso.Orders.Infrastructure.Persistence;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrdersDbContext).Assembly);
}
```

Why the expression-bodied `=> Set<T>()` form: the set is resolved by convention, so there is no
auto-property to forget to initialize and no null-forgiving operator, and the model is always applied
from the same assembly the configurations live in.

## Entity configuration

```csharp
namespace Contoso.Orders.Infrastructure.Persistence.Configurations.Orders;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> entity)
    {
        entity.ToTable("orders", "dbo");
        entity.HasKey(e => e.Id).HasName("pk_orders");

        entity.Property(e => e.Reference).HasColumnName("reference")
              .HasMaxLength(40).IsUnicode(false).IsRequired();
        entity.Property(e => e.State).HasColumnName("state")
              .HasMaxLength(20).IsUnicode(false)
              .HasConversion<string>();            // readable, renumber-safe
        entity.Property(e => e.PlacedAt).HasColumnName("placed_at")
              .HasColumnType("datetime2(3)");

        entity.HasIndex(e => e.Reference).IsUnique().HasDatabaseName("ix_orders_reference");
    }
}
```

Non-generated audit columns — the single most-missed line in bulk-write code:

```csharp
entity.Property(e => e.ExecutedAt).HasColumnName("executed_at")
      .HasColumnType("datetime2(3)")
      .ValueGeneratedOnAdd()                    // without this, bulk writes store 0001-01-01
      .HasDefaultValueSql("SYSUTCDATETIME()");
```

Why: EF sends the CLR default value for every column in the insert, so a column with a SQL `DEFAULT`
is only defaulted by a hand-written `INSERT` that omits it. Bulk libraries include every column, so you
get `0001-01-01`. See `references/14-production-pitfalls.md`.

## Module Context

```csharp
namespace Contoso.Orders.Infrastructure.Persistence.Contexts.Orders;

public interface IOrdersContext
{
    Task<IReadOnlyList<Order>> GetByStatesAsync(IReadOnlyCollection<OrderState> states, CancellationToken cancellationToken);
    Task UpsertRangeAsync(IReadOnlyList<Order> orders, CancellationToken cancellationToken);
}
```

```csharp
public sealed class OrdersContext(IDbContextFactory<OrdersDbContext> factory) : IOrdersContext
{
    public async Task<IReadOnlyList<Order>> GetByStatesAsync(
        IReadOnlyCollection<OrderState> states, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Orders
            .AsNoTracking()
            .Where(o => states.Contains(o.State))
            .OrderBy(o => o.Reference)
            .ToListAsync(cancellationToken);
    }
}
```

Create the context, query, dispose — never keep one field per context, never chain continuations.

Why `IDbContextFactory` and not `AddDbContext<T>`: a `BackgroundService` or batch writer that opens
its own context needs a factory, and having the factory everywhere means no code path can accidentally
reuse a context across concurrent operations.

## Registration

```csharp
public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SqlOptions>(configuration.GetSection(SqlOptions.SectionName));

        services.AddDbContextFactory<OrdersDbContext>((sp, options) =>
        {
            var sql = sp.GetRequiredService<IOptions<SqlOptions>>().Value;
            options.UseSqlServer(sql.ConnectionString, b => b.EnableRetryOnFailure());
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLogContext, AuditLogContext>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddHostedService<AuditBatchWriter>();

        return services;
    }
}
```

`EnableRetryOnFailure` is not optional on SQL Server: transient connection resets are normal under
Azure networking, and without it each one surfaces as a 500. It has a consequence you must respect —
see the execution-strategy rule below.

Connection string authentication: use **Microsoft Entra ID** (managed identity in the cloud,
`Authentication=Active Directory …` locally). Never a user/password pair, which turns every deployment
into a credential to rotate.

## Bulk writes — the four rules

`EFCore.BulkExtensions` is the fastest way to write batches, and it has four behaviours that only
appear against a real SQL Server.

```csharp
var strategy = db.Database.CreateExecutionStrategy();

await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

    var headers = await BuildHeadersAsync(db, batch, now, cancellationToken);
    await db.BulkInsertOrUpdateAsync(
        headers,
        new BulkConfig
        {
            UpdateByProperties = [nameof(Order.Reference)],
            UseTempDB = true,          // REQUIRED: see rule 1
        },
        cancellationToken: cancellationToken);

    var details = batch.Select(e => BuildEntry(e, now)).ToList();
    await db.AuditLogEntries.AddRangeAsync(details, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken);
});
```

1. **`UseTempDB = true`.** Without it, the library creates and drops a **real staging table with a real
   name in your schema** on every batch, which requires `CREATE TABLE`/`DROP TABLE` permission. A
   low-privilege login fails with a permission error that looks nothing like "you need more
   permissions".
2. **`UseTempDB = true` requires an explicit transaction.** On a real SQL Server, omitting it throws
   `InvalidOperationException`.
3. **Wrap in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`.** With
   `EnableRetryOnFailure` configured, a bare `BeginTransactionAsync()` throws immediately — the
   execution strategy and user-initiated transactions are mutually exclusive by design.
4. **None of 1–3 reproduce on SQLite.** The unit tests will pass. Verify against a real SQL Server and
   watch the rows actually move. Full write-up in `references/14-production-pitfalls.md`.

## Anti-patterns

- `IRepository<T>` / `GenericContext<T>` with a generic `GetAllAsync` / `SaveAsync`.
- `[Table]` attributes on the domain entity instead of a configuration class.
- `AddDbContext<T>` in a project that also has a batch writer, forcing a scope hack.
- `EnableRetryOnFailure()` plus a hand-started transaction not wrapped in the execution strategy.
- Bulk without `UseTempDB = true`, or `UseTempDB = true` without a transaction.
- A column with a SQL `DEFAULT` and no `ValueGeneratedOnAdd()`/`HasDefaultValueSql()`.
- `db.Orders.Add(...)` inside a loop instead of a batch.
- A `DbContext` injected as Scoped into a `BackgroundService` that outlives the scope.
- `AsNoTracking()` forgotten on read-only queries.
- Testing a bulk path only on SQLite and declaring it done.
