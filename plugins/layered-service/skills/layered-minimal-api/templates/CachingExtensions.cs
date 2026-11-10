// Optional cache: HybridCache (in-memory first level) with Redis as the second level ONLY when a
// Redis connection string exists. Redis is an option, never a default - do not copy this file into a
// service unless references/17-caching-and-redis.md says a cache is justified.
//
// `Contoso.Orders` is the example namespace - replace it with `<Company>.<Product>` before building.
// Packages (Api project): Microsoft.Extensions.Caching.Hybrid, Microsoft.Extensions.Caching.StackExchangeRedis.
//
// Required adaptation, NOT done here: TTL jitter (so many keys do not expire together) and an
// entry-count bound. MaximumPayloadBytes caps ONE value; it is not a total memory limit.
using Microsoft.Extensions.Caching.Hybrid;
using StackExchange.Redis;

namespace Contoso.Orders.Api.Caching;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Redis connection string. A secret: user secrets locally, Key Vault or app settings in prod. Empty = no Redis.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Upper bound for any entry, in the shared cache.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one instance keeps its own in-memory copy. Keep it short: it is the staleness window between instances.</summary>
    public TimeSpan LocalTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest single value cached, in bytes. Bigger values are not cached. Not a total memory limit.</summary>
    public long MaximumPayloadBytes { get; set; } = 1_048_576;

    /// <summary>Connect, sync and async timeout for Redis. Short: a slow Redis must not make the API slow.</summary>
    public TimeSpan RedisTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}

public static class CachingExtensions
{
    public static IServiceCollection AddCaching(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CacheOptions.SectionName);

        // Fails at startup, not on the first request (references/11). Redis stays optional.
        services.AddOptions<CacheOptions>()
            .Bind(section)
            .Validate(IsValid, "Cache: TTLs and RedisTimeout must be positive, LocalTtl <= DefaultTtl, MaximumPayloadBytes > 0.")
            .ValidateOnStart();

        // Read once here only to decide which backend to register.
        var options = section.Get<CacheOptions>() ?? new CacheOptions();

        // Register the Redis backend only when a non-blank connection string exists; always register
        // HybridCache. With no Redis the service still starts and the cache is in-memory only - a
        // missing setting must not take the API down.
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                var config = ConfigurationOptions.Parse(options.RedisConnectionString);
                var timeoutMs = (int)options.RedisTimeout.TotalMilliseconds;
                config.AbortOnConnectFail = false; // start even if Redis is down; reconnect later
                config.ConnectTimeout = timeoutMs;
                config.SyncTimeout = timeoutMs;
                config.AsyncTimeout = timeoutMs;
                redis.ConfigurationOptions = config;
            });
        }

        services.AddHybridCache(hybrid =>
        {
            hybrid.MaximumPayloadBytes = options.MaximumPayloadBytes;
            // Every entry has an expiry. No entry without a TTL (references/17, rule 2).
            hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = options.DefaultTtl,
                LocalCacheExpiration = options.LocalTtl,
            };
        });

        return services;
    }

    private static bool IsValid(CacheOptions o) =>
        o.DefaultTtl > TimeSpan.Zero
        && o.LocalTtl > TimeSpan.Zero
        && o.LocalTtl <= o.DefaultTtl
        && o.MaximumPayloadBytes > 0
        && o.RedisTimeout > TimeSpan.Zero;
}
