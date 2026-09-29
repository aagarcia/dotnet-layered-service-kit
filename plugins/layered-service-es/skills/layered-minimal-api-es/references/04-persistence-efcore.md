# 04 — Persistencia con EF Core

## La regla

Un `DbContext`. Un `IEntityTypeConfiguration<T>` por entidad en `Configurations/<Module>/`. Un
`Context` por módulo — **nunca un `IRepository<T>` genérico**. Todo el registro en
`AddPersistence(services, configuration)`.

Por qué no un repositorio genérico: un `IRepository<T>` genérico promete que cualquier entidad se
puede consultar y guardar en cualquier parte, lo cual es falso para la mayoría, y además oculta la
consulta real — no puedes leer el SQL sin entrar al repositorio. Un `Context` de módulo es una lista
escrita a mano de las operaciones que el módulo realmente necesita, y cada una es una consulta que se
puede revisar.

**La persistencia es opcional.** Un receptor puro que valida, normaliza y encola no necesita base de
datos en absoluto. Si la única razón para tener un `DbContext` es una tabla de auditoría, dilo en
`CLAUDE.md` en vez de fingir que el dominio es relacional.

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

Por qué la forma con cuerpo de expresión `=> Set<T>()`: el set se resuelve por convención, así que no
hay auto-property que se olvide inicializar ni operador null-forgiving, y el modelo siempre se aplica
desde el mismo assembly donde viven las configuraciones.

## Configuración de la entidad

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
              .HasConversion<string>();            // legible, seguro ante renumeración
        entity.Property(e => e.PlacedAt).HasColumnName("placed_at")
              .HasColumnType("datetime2(3)");

        entity.HasIndex(e => e.Reference).IsUnique().HasDatabaseName("ix_orders_reference");
    }
}
```

Columnas de auditoría no generadas — la línea que más se olvida en el código de escritura por lotes:

```csharp
entity.Property(e => e.ExecutedAt).HasColumnName("executed_at")
      .HasColumnType("datetime2(3)")
      .ValueGeneratedOnAdd()                    // sin esto, la escritura por lotes guarda 0001-01-01
      .HasDefaultValueSql("SYSUTCDATETIME()");
```

Por qué: EF envía el valor por defecto del CLR para cada columna del insert, así que una columna con
un `DEFAULT` de SQL solo recibe ese valor por defecto desde un `INSERT` escrito a mano que la omite.
Las librerías de escritura por lotes incluyen todas las columnas, así que terminas con `0001-01-01`.
Ver `references/14-production-pitfalls.md`.

## Context del módulo

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

Crea el context, consulta, descarta — nunca guardes un campo por context, nunca encadenes
continuaciones.

Por qué `IDbContextFactory` y no `AddDbContext<T>`: un `BackgroundService` o un escritor de lotes
que abre su propio context necesita una factory, y tener la factory en todas partes significa que
ningún camino de código puede reutilizar por accidente un context entre operaciones concurrentes.

## Registro

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

`EnableRetryOnFailure` no es opcional en SQL Server: los reinicios transitorios de conexión son
normales bajo la red de Azure, y sin él cada uno se manifiesta como un 500. Tiene una consecuencia
que debes respetar — ver la regla de la estrategia de ejecución más abajo.

Autenticación de la cadena de conexión: usa **Microsoft Entra ID** (identidad administrada en la nube,
`Authentication=Active Directory …` en local). Nunca un par usuario/contraseña, que convierte cada
despliegue en una credencial que hay que rotar.

## Escritura por lotes: las cuatro reglas

`EFCore.BulkExtensions` es la forma más rápida de escribir lotes, y tiene cuatro comportamientos que
solo aparecen contra un SQL Server real.

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
            UseTempDB = true,          // OBLIGATORIO: ver la regla 1
        },
        cancellationToken: cancellationToken);

    var details = batch.Select(e => BuildEntry(e, now)).ToList();
    await db.AuditLogEntries.AddRangeAsync(details, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken);
});
```

1. **`UseTempDB = true`.** Sin él, la biblioteca crea y elimina una **tabla de staging real con un
   nombre real en tu esquema** en cada lote, lo que requiere permiso de `CREATE TABLE`/`DROP TABLE`.
   Un login de pocos privilegios falla con un error de permisos que no se parece en nada a «necesitas
   más permisos».
2. **`UseTempDB = true` requiere una transacción explícita.** En un SQL Server real, omitirla lanza
   `InvalidOperationException`.
3. **Envuelve en `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`.** Con
   `EnableRetryOnFailure` configurado, un `BeginTransactionAsync()` a secas lanza de inmediato — la
   estrategia de ejecución y las transacciones iniciadas por el usuario son mutuamente excluyentes
   por diseño.
4. **Ninguno de los puntos 1 a 3 se reproduce en SQLite.** Las pruebas unitarias van a pasar. Verifica
   contra un SQL Server real y comprueba que las filas realmente se muevan. Explicación completa en
   `references/14-production-pitfalls.md`.

## Anti-patrones

- `IRepository<T>` / `GenericContext<T>` con un `GetAllAsync` / `SaveAsync` genérico.
- Atributos `[Table]` en la entidad de dominio en vez de una clase de configuración.
- `AddDbContext<T>` en un proyecto que además tiene un escritor de lotes, lo que obliga a un truco de
  scope.
- `EnableRetryOnFailure()` más una transacción iniciada a mano que no está envuelta en la estrategia de
  ejecución.
- Escritura por lotes sin `UseTempDB = true`, o `UseTempDB = true` sin transacción.
- Una columna con un `DEFAULT` de SQL y sin `ValueGeneratedOnAdd()`/`HasDefaultValueSql()`.
- `db.Orders.Add(...)` dentro de un loop en vez de un lote.
- Un `DbContext` inyectado como Scoped en un `BackgroundService` que vive más que el scope.
- `AsNoTracking()` olvidado en consultas de solo lectura.
- Probar una ruta de escritura por lotes solo en SQLite y darla por terminada.
