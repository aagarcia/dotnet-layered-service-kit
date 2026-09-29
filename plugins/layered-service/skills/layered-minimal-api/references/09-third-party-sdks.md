# 09 — Third-party SDKs

## The rule

A vendor SDK lives in `Api/Services/<Provider>/` and **nowhere else**. Only that folder references the
package. The port interface lives in `Application.UseCases/IServices/`.

Why: the moment an SDK type crosses into a lower layer, that layer cannot be built, tested or reasoned
about without the package, and the tests start needing a real account. Keep the SDK behind the port and
the rest of the service never learns its name.

## Registration

```csharp
public static class CarrierServiceCollectionExtensions
{
    public static IServiceCollection AddCarrier(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CarrierOptions>(configuration.GetSection(CarrierOptions.SectionName));

        services.AddSingleton<CarrierApiProvider>();               // the one session-holding instance
        services.AddSingleton<CarrierDeviceDirectory>();
        services.AddSingleton<IShipmentDirectory>(sp => sp.GetRequiredService<CarrierDeviceDirectory>());
        services.AddSingleton<ICarrierService, CarrierService>();
        services.AddHostedService<DeviceDirectoryRefresher>();

        return services;
    }
}
```

One extension method per provider. When a second integration arrives, it gets its own extension and its
own options section — it does not get appended to the first one.

If the provider is not configured, **the API still starts**: register the services, have the provider
report `IsConfigured == false`, and have the endpoints answer `503`. A missing credential for one
integration must not take the whole service down.

## The provider: one singleton, its own handler, a login lock

```csharp
namespace Contoso.Orders.Api.Services.Carrier;

/// <summary>
/// The single SDK client instance per process. The SDK holds the session and re-authenticates on
/// expiry, but its first authentication is not guarded: without the semaphore, N concurrent requests
/// fire N logins and the provider throttles auth. It also creates an HttpClient per call if handed an
/// HttpMessageHandler and never disposes it, so it gets one process-lifetime SocketsHttpHandler —
/// not one from IHttpClientFactory, which rotates and would be retained forever.
/// </summary>
public sealed class CarrierApiProvider : IDisposable
{
    private readonly SocketsHttpHandler _handler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly CarrierApi? _api;
    private volatile bool _authenticated;

    public CarrierApiProvider(IOptions<CarrierOptions> options)
    {
        var carrier = options.Value;
        if (carrier.IsConfigured)
        {
            _api = new CarrierApi(carrier.UserName, carrier.Password, carrier.Database, carrier.Server,
                (int)TimeSpan.FromSeconds(carrier.TimeoutSeconds).TotalMilliseconds, _handler);
        }
    }

    public bool IsConfigured => _api is not null;

    public async Task<CarrierApi> GetApiAsync(CancellationToken cancellationToken)
    {
        if (_api is null)
            throw new CarrierServiceException(CarrierErrorType.Unavailable, "Carrier integration is not configured.");

        if (_authenticated) return _api;

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (!_authenticated)
            {
                await _api.AuthenticateAsync(cancellationToken);
                _authenticated = true;
            }
        }
        finally { _authLock.Release(); }

        return _api;
    }

    public void Dispose()
    {
        _handler.Dispose();
        _authLock.Dispose();
    }
}
```

Why each piece:

- **Singleton** — the SDK keeps session state. `new CarrierApi(...)` per request means duplicate
  sockets, duplicate logins, and duplicated connection counts against the provider's limits.
- **Its own `SocketsHttpHandler`** — the SDK holds the handler for its lifetime, so a factory-managed
  one is never rotated and eventually its pooled connections are the wrong age.
- **`SemaphoreSlim` on first login** — the SDK has no auth lock of its own.
- **No `PublishAot` / `PublishTrimmed`** if the SDK serializes by reflection. It compiles, publishes and
  then throws `Reflection-based serialization has been disabled` at runtime.

## Error translation is a pure class

```csharp
namespace Contoso.Orders.Api.Services.Carrier;

public static class CarrierErrorTranslator
{
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(1);

    public static CarrierServiceException? Translate(Exception exception, CancellationToken callerCancellation) =>
        exception switch
        {
            OverLimitException => new CarrierServiceException(
                CarrierErrorType.LimitExceeded, "Carrier query limit exceeded.",
                GetRetryAfter(exception), exception),
            InvalidUserException => Unavailable("Carrier credentials rejected.", exception),
            ServiceUnavailableException or TransientHttpErrorException => Unavailable("Carrier is unavailable.", exception),
            HttpRequestException => Unavailable("No connection to the carrier.", exception),
            TimeoutException => Unavailable("The carrier did not respond in time.", exception),
            // The HttpClient timeout arrives as TaskCanceledException without the caller cancelling.
            OperationCanceledException when !callerCancellation.IsCancellationRequested =>
                Unavailable("The carrier did not respond in time.", exception),
            _ => null,      // not a provider/network failure: let it propagate unchanged
        };

    private static CarrierServiceException Unavailable(string message, Exception inner) =>
        new(CarrierErrorType.Unavailable, message, innerException: inner);
}
```

Returning `null` for "not mine" is the important design decision: the translator is a filter, not a
catch-all, so a genuine bug in your own code is not disguised as a provider outage.

The handler then never sees an SDK exception; it sees a small enum and translates that into a result
DTO. `LimitExceeded` becomes `429` with `Retry-After`, `Unavailable` becomes `503`.

## In-memory index built by a pure class

When a live lookup per request is too slow or too expensive, build an index once and serve from memory.

```csharp
// PURE: takes a flat list, returns a lookup. No SDK, no HTTP.
public static class DeviceIndexBuilder
{
    public static DeviceIndex Build(IReadOnlyList<CarrierDevice> devices, DateTimeOffset loadedAt)
    {
        var byCode = new Dictionary<string, CarrierDevice>(StringComparer.OrdinalIgnoreCase);
        var duplicateCodes = new List<string>();

        foreach (var device in devices)
        {
            var code = device.Code.Trim();
            if (code.Length == 0) continue;
            if (!byCode.TryAdd(code, device)) duplicateCodes.Add(code);
        }

        var ordered = byCode.Values
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)      // stable tie-break: a stable page
            .ToList();

        return new DeviceIndex(byCode, ordered, loadedAt, duplicateCodes);
    }
}
```

The pure builder reports duplicate codes instead of silently keeping the last one, and it sorts with an
explicit tie-break so a paginated view does not shuffle between requests.

The refresher that feeds it never throws:

```csharp
public sealed class DeviceDirectoryRefresher(
    CarrierDeviceDirectory directory, CarrierApiProvider provider,
    IOptions<CarrierOptions> options, TimeProvider timeProvider,
    ILogger<DeviceDirectoryRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!provider.IsConfigured)
        {
            logger.LogWarning("Carrier section has no credentials: /carrier/* will answer 503.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.DirectoryMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            await TryReloadAsync(stoppingToken);
            try { await Task.Delay(directory.IsLoaded ? interval : RetryDelay, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TryReloadAsync(CancellationToken stoppingToken)
    {
        try
        {
            var count = await directory.ReloadAsync(stoppingToken);
            logger.LogInformation("Device index loaded: {Count} entries.", count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            // Keep the previous index: a refresh failure must not empty a working cache.
            logger.LogWarning(ex, "Could not refresh the device index; keeping the previous one.");
        }
    }
}
```

Serving behaviour: while no load has ever succeeded answer `503`; afterwards serve the last good
snapshot. An endpoint that returns an empty list because the cache is cold is indistinguishable from
"there is nothing", which is a data-quality bug.

## Pagination is pure logic

```csharp
public static class Pagination
{
    public const int PageDefault = 1;
    public const int PageSizeDefault = 20;

    public static string? Validate(int page, int pageSize, int maxPageSize)
    {
        if (page < 1) return "page must be greater than or equal to 1.";
        return pageSize < 1 || pageSize > maxPageSize
            ? $"pageSize must be between 1 and {maxPageSize}." : null;
    }

    public static PagedResult<TOut> Page<TIn, TOut>(
        IReadOnlyList<TIn> source, int page, int pageSize, DateTimeOffset refreshedAt, Func<TIn, TOut> map)
    {
        // In long: page * pageSize overflows an int when page is int.MaxValue.
        var skip = (long)(page - 1) * pageSize;
        var total = source.Count;

        var items = skip >= total
            ? []
            : source.Skip((int)skip).Take(pageSize).Select(map).ToList();

        return new PagedResult<TOut>(page, pageSize, total,
            total == 0 ? 0 : (total + pageSize - 1) / pageSize, refreshedAt, items);
    }
}
```

Validate **before** touching the cache, so a bad request is a `400` regardless of cache state.

## Decompile the SDK before designing

Do not design against a README. Decompile the package and design against what it actually exposes:

```bash
dotnet tool install --global ilspycmd
ilspycmd -p ./nuget-cache/<package> -o ./sdk-source
```

Look for: does it hold a session or authenticate per call? does it create an `HttpClient` per call?
does it have an auth lock? is serialization reflection-based (AOT-unsafe)? what are its rate limits and
its error types, and which of them are recoverable? An hour of reading the decompiled source is cheaper
than one production incident caused by a wrong assumption.

## Anti-patterns

- An SDK type appearing in a signature in `Domain`, `Application.UseCases` or `Persistence`.
- `new CarrierApi(...)` in a handler, or the client registered Scoped or Transient.
- A handler from `IHttpClientFactory` handed to a session-holding SDK.
- Translating SDK errors inside the client class, so the handler imports the SDK to interpret them.
- A translator that catches everything, turning your own bugs into `503`.
- A cache whose refresh failure empties it.
- An endpoint returning an empty list when the cache is cold, indistinguishable from "no data".
- Pagination logic inline in an endpoint.
- `PublishAot` or `PublishTrimmed` enabled with a reflection-based SDK.
- Copying another provider's flow into a new provider. A push provider and a pull provider want
  different designs; reuse the **structure and the conventions**, not the flow.
- Reusing a working endpoint's design wholesale for a provider with different limits and semantics.
