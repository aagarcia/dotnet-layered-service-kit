// Una caché como DECORATOR detrás de un puerto - nunca dentro del handler, el dominio ni la
// configuración de entidades (references/02, references/17). El lector interno es la shell de I/O
// delgada que habla con la base de datos o el SDK del proveedor; esta clase solo decide llaves e
// invalidación.
//
// `Contoso.Orders` es el namespace de ejemplo - reemplázalo por `<Company>.<Product>` antes de compilar.
// El puerto, el DTO y los contadores de telemetría de abajo son ejemplos que defines en tu service:
//   IOrderStatusReader  - Application.UseCases/IServices
//   OrderStatusDto      - Application.UseCases/Commons/DTOs
//   OrderTelemetry.CacheSourceLoads / CacheInvalidationFailures - Counter<long> en Domain/Telemetry (references/03, 10)
//
// Fallas de Redis: HybridCache ya registra en el log una falla de lectura de L2 (Redis) y la trata
// como miss, y registra y traga una falla de escritura de L2. NO envuelvas GetOrCreateAsync en un
// catch amplio: los llamadores concurrentes comparten UNA ejecución del factory, así que una bandera
// local al llamador no distingue una falla de la caché de una falla de la fuente. Observa Redis con los
// logs y métricas del backend/HybridCache; cuenta las cargas de la fuente dentro del factory.
using Contoso.Orders.Application.UseCases.Commons.DTOs;
using Contoso.Orders.Application.UseCases.IServices;
using Contoso.Orders.Domain.Telemetry;
using Microsoft.Extensions.Caching.Hybrid;

namespace Contoso.Orders.Api.Caching;

/// <summary>Pura y testeable por unidad: construye llaves y tags. Sin I/O.</summary>
public static class OrderCacheKeys
{
    // Prefijo de versión: súbelo para invalidar todas las entradas viejas tras cambiar un DTO. Si varios
    // services o ambientes comparten un Redis, agrega aquí también un namespace de service/ambiente.
    private const string Prefix = "orders:v1";

    // Cada componente se escapa, así que un separador dentro de un valor no puede hacer que dos pares
    // (tenant, order) distintos produzcan la misma llave. El tenant DEBE venir del contexto del
    // llamador autorizado (el token), nunca de un campo del request que el llamador controle. Agrega
    // también el id de usuario cuando el resultado varíe por usuario.
    public static string Status(string tenantId, string orderId) =>
        $"{Prefix}:{Escape(tenantId)}:status:{Escape(orderId)}";

    // Etiqueta todas las entradas de un tenant para soltarlas juntas con una llamada (RemoveByTagAsync).
    public static string TenantTag(string tenantId) => $"{Prefix}:{Escape(tenantId)}";

    private static string Escape(string value) => Uri.EscapeDataString(value);
}

public sealed class CachedOrderStatusReader(
    IOrderStatusReader inner,
    HybridCache cache,
    ILogger<CachedOrderStatusReader> logger) : IOrderStatusReader
{
    public async Task<OrderStatusDto?> GetAsync(
        string tenantId, string orderId, CancellationToken cancellationToken) =>
        // HybridCache da protección contra stampede dentro de UN proceso: uno carga, los demás
        // esperan. No es distribuida entre réplicas.
        await cache.GetOrCreateAsync(
            OrderCacheKeys.Status(tenantId, orderId),
            async ct =>
            {
                OrderTelemetry.CacheSourceLoads.Add(1);
                return await inner.GetAsync(tenantId, orderId, ct);
            },
            tags: [OrderCacheKeys.TenantTag(tenantId)],
            cancellationToken: cancellationToken);

    // Llama a esto DESPUÉS del commit final a la base de datos (no dentro de una transacción explícita
    // que aún no confirmó): SaveChanges, ExecuteUpdate o ExecuteDelete nunca invalidan por sí solos.
    //
    // Es de mejor esfuerzo. La escritura en la base de datos ya tuvo éxito, así que un error de Redis
    // aquí NO debe hacer fallar la respuesta HTTP ni dar a entender que la escritura se revirtió:
    // regístralo, cuéntalo y deja que el TTL acote la desactualización. Donde la desactualización no
    // sea aceptable, agrega un reintento durable (outbox) - no incluido aquí.
    // Quitar una llave no limpia las copias en memoria de otras instancias (viven hasta LocalTtl), y una
    // lectura ya en curso puede repoblar el valor viejo. Invalidar después del commit es el orden
    // correcto; no es consistencia inmediata.
    public async ValueTask InvalidateAsync(
        string tenantId, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(OrderCacheKeys.Status(tenantId, orderId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falló la invalidación de caché para el tenant {TenantId}; las entradas expiran por TTL", tenantId);
            OrderTelemetry.CacheInvalidationFailures.Add(1);
        }
    }

    // La invalidación por tag es lógica (las entradas se tratan como expiradas), no un borrado físico.
    public ValueTask InvalidateTenantAsync(string tenantId, CancellationToken cancellationToken) =>
        cache.RemoveByTagAsync(OrderCacheKeys.TenantTag(tenantId), cancellationToken);
}
