// Caché opcional: HybridCache (primer nivel en memoria) con Redis como segundo nivel SOLO cuando existe
// una cadena de conexión de Redis. Redis es una opción, nunca un default - no copies este archivo a un
// service salvo que references/17-caching-and-redis.md diga que una caché se justifica.
//
// `Contoso.Orders` es el namespace de ejemplo - reemplázalo por `<Company>.<Product>` antes de compilar.
// Paquetes (proyecto Api): Microsoft.Extensions.Caching.Hybrid, Microsoft.Extensions.Caching.StackExchangeRedis.
//
// Adaptación requerida, NO incluida aquí: jitter en el TTL (para que muchas llaves no expiren juntas) y
// un tope de cantidad de entradas. MaximumPayloadBytes limita UN valor; no es un límite total de memoria.
using Microsoft.Extensions.Caching.Hybrid;
using StackExchange.Redis;

namespace Contoso.Orders.Api.Caching;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Cadena de conexión de Redis. Es un secreto: user secrets en local, Key Vault o app settings en prod. Vacía = sin Redis.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Tope de cualquier entrada en la caché compartida.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cuánto guarda cada instancia su copia en memoria. Mantenlo corto: es la ventana de desactualización entre instancias.</summary>
    public TimeSpan LocalTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Valor individual más grande que se cachea, en bytes. Los valores mayores no se cachean. No es un límite total de memoria.</summary>
    public long MaximumPayloadBytes { get; set; } = 1_048_576;

    /// <summary>Timeout de conexión, sync y async hacia Redis. Corto: un Redis lento no debe volver lenta la API.</summary>
    public TimeSpan RedisTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}

public static class CachingExtensions
{
    public static IServiceCollection AddCaching(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CacheOptions.SectionName);

        // Falla al arrancar, no en el primer request (references/11). Redis sigue siendo opcional.
        services.AddOptions<CacheOptions>()
            .Bind(section)
            .Validate(IsValid, "Cache: los TTL y RedisTimeout deben ser positivos, LocalTtl <= DefaultTtl, MaximumPayloadBytes > 0.")
            .ValidateOnStart();

        // Se lee una vez aquí solo para decidir qué backend registrar.
        var options = section.Get<CacheOptions>() ?? new CacheOptions();

        // Registra el backend de Redis solo cuando existe una cadena de conexión no vacía; HybridCache
        // se registra siempre. Sin Redis el service arranca igual y la caché es solo en memoria - un
        // setting faltante no debe tumbar la API.
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                var config = ConfigurationOptions.Parse(options.RedisConnectionString);
                var timeoutMs = (int)options.RedisTimeout.TotalMilliseconds;
                config.AbortOnConnectFail = false; // arranca aunque Redis esté caído; reconecta después
                config.ConnectTimeout = timeoutMs;
                config.SyncTimeout = timeoutMs;
                config.AsyncTimeout = timeoutMs;
                redis.ConfigurationOptions = config;
            });
        }

        services.AddHybridCache(hybrid =>
        {
            hybrid.MaximumPayloadBytes = options.MaximumPayloadBytes;
            // Toda entrada tiene expiración. Ninguna entrada sin TTL (references/17, regla 2).
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
