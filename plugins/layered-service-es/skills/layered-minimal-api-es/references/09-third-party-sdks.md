# 09 — SDK de terceros

## La regla

Un SDK de proveedor vive en `Api/Services/<Provider>/` y **en ningún otro sitio**. Solo esa carpeta
referencia el paquete. La interfaz de puerto vive en `Application.UseCases/IServices/`.

Por qué: en el momento en que un tipo del SDK cruza a una capa inferior, esa capa ya no se puede
compilar, testear ni razonar sin el paquete, y los tests empiezan a necesitar una cuenta real.
Mantén el SDK detrás del puerto y el resto del servicio nunca conocerá su nombre.

## Registro

```csharp
public static class CarrierServiceCollectionExtensions
{
    public static IServiceCollection AddCarrier(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CarrierOptions>(configuration.GetSection(CarrierOptions.SectionName));

        services.AddSingleton<CarrierApiProvider>();               // la única instancia con sesión
        services.AddSingleton<CarrierDeviceDirectory>();
        services.AddSingleton<IShipmentDirectory>(sp => sp.GetRequiredService<CarrierDeviceDirectory>());
        services.AddSingleton<ICarrierService, CarrierService>();
        services.AddHostedService<DeviceDirectoryRefresher>();

        return services;
    }
}
```

Un método de extensión por proveedor. Cuando llega una segunda integración, recibe su propia extensión
y su propia sección de opciones — no se añade a la primera.

Si el proveedor no está configurado, **la API arranca igual**: registra los servicios, haz que el
proveedor informe `IsConfigured == false` y que los endpoints respondan `503`. Una credencial ausente
en una integración no debe tumbar todo el servicio.

## El proveedor: un singleton, su propio handler, un lock de login

```csharp
namespace Contoso.Orders.Api.Services.Carrier;

/// <summary>
/// La única instancia del cliente del SDK por proceso. El SDK mantiene la sesión y reautentica al
/// expirar, pero su primera autenticación no está protegida: sin el semáforo, N peticiones concurrentes
/// disparan N logins y el proveedor estrangula la autenticación. También crea un HttpClient por llamada
/// si se le pasa un HttpMessageHandler y nunca lo libera, así que recibe un SocketsHttpHandler con la
/// vida del proceso — no uno de IHttpClientFactory, que rota y quedaría retenido para siempre.
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

Por qué cada pieza:

- **Singleton** — el SDK conserva estado de sesión. `new CarrierApi(...)` por petición significa
  sockets duplicados, logins duplicados y conteos de conexión duplicados contra los límites del
  proveedor.
- **Su propio `SocketsHttpHandler`** — el SDK mantiene el handler durante toda su vida, así que uno
  gestionado por una factory nunca rota y sus conexiones agrupadas acaban con la edad equivocada.
- **`SemaphoreSlim` en el primer login** — el SDK no tiene un lock de autenticación propio.
- **Nada de `PublishAot` / `PublishTrimmed`** si el SDK serializa por reflexión. Compila, publica y
  luego lanza `Reflection-based serialization has been disabled` en tiempo de ejecución.

## La traducción de errores es una clase pura

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
            // El timeout del HttpClient llega como TaskCanceledException sin que el llamador haya cancelado.
            OperationCanceledException when !callerCancellation.IsCancellationRequested =>
                Unavailable("The carrier did not respond in time.", exception),
            _ => null,      // no es un fallo del proveedor ni de la red: déjalo propagarse sin cambios
        };

    private static CarrierServiceException Unavailable(string message, Exception inner) =>
        new(CarrierErrorType.Unavailable, message, innerException: inner);
}
```

Devolver `null` para «esto no es mío» es la decisión de diseño importante: el traductor es un filtro,
no un `catch-all`, así que un bug real en tu propio código no se disfraza de una caída del proveedor.

El handler nunca ve entonces una excepción del SDK; ve un enum pequeño y lo traduce a un DTO de
resultado. `LimitExceeded` se convierte en `429` con `Retry-After`, `Unavailable` en `503`.

## Índice en memoria construido por una clase pura

Cuando una consulta en vivo por petición es demasiado lenta o demasiado cara, construye el índice una
vez y sírvelo desde memoria.

```csharp
// PURA: recibe una lista plana y devuelve una búsqueda. Sin SDK, sin HTTP.
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
            .ThenBy(d => d.Id, StringComparer.Ordinal)      // desempate estable: una página estable
            .ToList();

        return new DeviceIndex(byCode, ordered, loadedAt, duplicateCodes);
    }
}
```

El constructor puro informa los códigos duplicados en vez de quedarse en silencio con el último, y
ordena con un desempate explícito para que una vista paginada no se mezcle entre peticiones.

El refresher que lo alimenta nunca lanza excepciones:

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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* apagado */ }
        catch (Exception ex)
        {
            // Mantén el índice anterior: un fallo de refresco no puede vaciar una caché que funciona.
            logger.LogWarning(ex, "Could not refresh the device index; keeping the previous one.");
        }
    }
}
```

Comportamiento al servir: mientras ninguna carga haya funcionado nunca, responder `503`; después,
servir la última instantánea buena. Un endpoint que devuelve una lista vacía porque la caché está
fría es indistinguible de «aquí no hay nada», y eso es un bug de calidad de datos.

## La paginación es lógica pura

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
        // En long: page * pageSize desborda un int cuando page es int.MaxValue.
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

Valida **antes** de tocar la caché, para que una petición inválida sea un `400` sea cual sea el estado
de la caché.

## Descompila el SDK antes de diseñar

No diseñes contra un README. Descompila el paquete y diseña contra lo que realmente expone:

```bash
dotnet tool install --global ilspycmd
ilspycmd -p ./nuget-cache/<package> -o ./sdk-source
```

Busca: ¿mantiene una sesión o autentica por llamada? ¿crea un `HttpClient` por llamada? ¿tiene un
lock de autenticación? ¿la serialización es por reflexión (inseguro con AOT)? ¿cuáles son sus límites
de tasa y sus tipos de error, y cuáles son recuperables? Una hora leyendo el fuente descompilado sale
más barata que un incidente en producción causado por una suposición equivocada.

## Anti-patrones

- Un tipo del SDK apareciendo en una firma en `Domain`, `Application.UseCases` o `Persistence`.
- `new CarrierApi(...)` dentro de un handler, o el cliente registrado como Scoped o Transient.
- Pasar al SDK, que mantiene sesión, un handler de `IHttpClientFactory`.
- Traducir errores del SDK dentro de la clase cliente, de modo que el handler importa el SDK para
  interpretarlos.
- Un traductor que captura todo y convierte tus propios bugs en `503`.
- Una caché que se vacía cuando falla su refresco.
- Un endpoint que devuelve una lista vacía cuando la caché está fría, indistinguible de «sin datos».
- Lógica de paginación en línea dentro de un endpoint.
- `PublishAot` o `PublishTrimmed` habilitados con un SDK basado en reflexión.
- Copiar el flujo de otro proveedor a un proveedor nuevo. Un proveedor de push y uno de pull quieren
  diseños distintos; reutiliza la **estructura y las convenciones**, no el flujo.
- Reutilizar tal cual el diseño de un endpoint que funciona para un proveedor con límites y
  semántica distintos.
