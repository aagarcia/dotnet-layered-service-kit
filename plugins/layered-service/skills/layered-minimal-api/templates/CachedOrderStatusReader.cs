// A cache as a DECORATOR behind a port - never inside the handler, the domain or the entity
// configuration (references/02, references/17). The inner reader is the thin I/O shell that talks to
// the database or the vendor SDK; this class only decides keys and invalidation.
//
// `Contoso.Orders` is the example namespace - replace it with `<Company>.<Product>` before building.
// The port, the DTO and the telemetry counters below are examples you define in your own service:
//   IOrderStatusReader  - Application.UseCases/IServices
//   OrderStatusDto      - Application.UseCases/Commons/DTOs
//   OrderTelemetry.CacheSourceLoads / CacheInvalidationFailures - Counter<long> in Domain/Telemetry (references/03, 10)
//
// Redis failures: HybridCache already logs an L2 (Redis) read failure and treats it as a miss, and
// logs and swallows an L2 write failure. Do NOT wrap GetOrCreateAsync in a broad catch: concurrent
// callers share ONE factory run, so a caller-local flag cannot tell a cache failure from a source
// failure. Watch Redis with the backend/HybridCache logs and metrics; count source loads in the factory.
using Contoso.Orders.Application.UseCases.Commons.DTOs;
using Contoso.Orders.Application.UseCases.IServices;
using Contoso.Orders.Domain.Telemetry;
using Microsoft.Extensions.Caching.Hybrid;

namespace Contoso.Orders.Api.Caching;

/// <summary>Pure and unit-testable: builds keys and tags. No I/O.</summary>
public static class OrderCacheKeys
{
    // Version prefix: bump it to invalidate every old entry after a DTO change. If several services
    // or environments share one Redis, add a service/environment namespace here too.
    private const string Prefix = "orders:v1";

    // Every component is escaped, so a separator inside a value cannot make two different
    // (tenant, order) pairs produce the same key. The tenant MUST come from the authorized caller
    // context (the token), never from a request field the caller controls. Add the user id too
    // whenever the result varies by user.
    public static string Status(string tenantId, string orderId) =>
        $"{Prefix}:{Escape(tenantId)}:status:{Escape(orderId)}";

    // Tag every entry of a tenant so one call can drop them together (RemoveByTagAsync).
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
        // HybridCache gives stampede protection inside ONE process: one caller loads, the others
        // wait for it. It is not distributed across replicas.
        await cache.GetOrCreateAsync(
            OrderCacheKeys.Status(tenantId, orderId),
            async ct =>
            {
                OrderTelemetry.CacheSourceLoads.Add(1);
                return await inner.GetAsync(tenantId, orderId, ct);
            },
            tags: [OrderCacheKeys.TenantTag(tenantId)],
            cancellationToken: cancellationToken);

    // Call this AFTER the final database commit (not inside an explicit transaction that has not
    // committed): SaveChanges, ExecuteUpdate or ExecuteDelete never invalidate by themselves.
    //
    // This is best effort. The database write already succeeded, so a Redis error here must NOT fail
    // the HTTP response or imply the write rolled back: log it, count it, and let the TTL bound the
    // staleness. Where staleness is not acceptable, add a durable retry (outbox) - not done here.
    // Removing a key does not clear other instances' in-memory copies (they live until LocalTtl),
    // and a read already in flight can repopulate the old value. Post-commit invalidation is the
    // right order; it is not immediate consistency.
    public async ValueTask InvalidateAsync(
        string tenantId, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(OrderCacheKeys.Status(tenantId, orderId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cache invalidation failed for tenant {TenantId}; entries expire by TTL", tenantId);
            OrderTelemetry.CacheInvalidationFailures.Add(1);
        }
    }

    // Tag invalidation is logical (entries are treated as expired), not a physical delete.
    public ValueTask InvalidateTenantAsync(string tenantId, CancellationToken cancellationToken) =>
        cache.RemoveByTagAsync(OrderCacheKeys.TenantTag(tenantId), cancellationToken);
}
