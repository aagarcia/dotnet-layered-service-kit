# 07 — Seguridad y limitación de tasa

## JWT: usa siempre el framework

```csharp
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwt);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];   // el descubrimiento OIDC es automático
        options.Audience = jwt["Audience"];
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
```

`Authority` + `Audience` es toda la configuración. El middleware obtiene las claves de firma del
documento de descubrimiento del proveedor de identidad y las cachea, refresca los tokens expirados y
responde `401` por sí mismo. **Nunca escribas tu propio validador de JWT.** Uno hecho a mano no tiene
rotación de claves, tolerancia de desfase de reloj, comprobación de audience ni defensa frente a
confusión de algoritmos, y nada de eso se ve en una revisión.

Forma típica: tokens de máquina a máquina emitidos con el flujo `client_credentials`.

```json
{ "Jwt": { "Authority": "https://login.example.com/<tenant>/v2.0", "Audience": "api://contoso-orders" } }
```

## Denegar por defecto

`SetFallbackPolicy(RequireAuthenticatedUser())` significa que cada ruta exige un token válido a menos
que se excluya con `.AllowAnonymous()`. No encadenes `.RequireAuthorization()` grupo por grupo; el
valor por defecto ya es «denegado», así que un endpoint añadido más tarde queda seguro por accidente y
no por descuido.

`.AllowAnonymous()` corresponde exactamente a dos cosas: el endpoint de salud y un endpoint de login
**si esta API es la propia emisora de tokens**.

## Si la API emite tokens

La mayoría de las APIs de servicio a servicio no emiten tokens; validan tokens emitidos en otro lado.
Solo cuando la API es realmente la emisora, emite el token desde un endpoint:

- la interfaz `IJwtTokenGenerator` vive en `Application.UseCases/IServices/` (así los handlers pueden
  depender de ella sin que esa capa referencie un paquete de JWT);
- la implementación, que necesita `System.IdentityModel.Tokens.Jwt`, vive en `Api/Services/`;
- el endpoint es `.AllowAnonymous()` y devuelve el token en el cuerpo — sin cookie.

Mantén la clave de firma fuera del `appsettings.json` versionado (ver
`references/11-configuration.md`).

## Policies propias: solo lo que el framework no tiene

ASP.NET Core puede expresar «autenticado». No puede expresar «autenticado **y** emitido para este
cliente concreto». Ese hueco es la única razón legítima para escribir código de autorización propio:
la distinción entre `401` y `403`.

```csharp
namespace Contoso.Orders.Api.Security;

/// <summary>
/// Exige que el token ya validado venga del registro de aplicación esperado
/// (claims "azp"/"appid") y no de cualquier otra aplicación del tenant que tenga un token para
/// nuestro audience. Falla como 403, nunca como 401: el token autenticó, simplemente el
/// llamador es el equivocado.
/// </summary>
public sealed class RequiredClientIdRequirement(string? requiredClientId) : IAuthorizationRequirement
{
    public string? RequiredClientId { get; } = requiredClientId;
}
```

```csharp
public sealed class RequiredClientIdHandler(ILogger<RequiredClientIdHandler> logger)
    : AuthorizationHandler<RequiredClientIdRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, RequiredClientIdRequirement requirement)
    {
        if (string.IsNullOrEmpty(requirement.RequiredClientId))
        {
            context.Succeed(requirement);   // no configurado: la policy no hace nada, por diseño
            return Task.CompletedTask;
        }

        var azp = context.User.FindFirst("azp")?.Value;
        var appid = context.User.FindFirst("appid")?.Value;

        if (string.Equals(azp, requirement.RequiredClientId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(appid, requirement.RequiredClientId, StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
        else
        {
            // Registra el motivo, nunca el token: el token es una credencial y no va en el log de nadie.
            logger.LogWarning(
                "Valid token from an unauthorized client (expected: {RequiredClientId}).",
                requirement.RequiredClientId);
        }

        return Task.CompletedTask;
    }
}
```

Ante un fallo de autenticación, registra el motivo mediante `JwtBearerEvents.OnAuthenticationFailed`.
Registra el evento, la excepción y el tipo de fallo — nunca el token en bruto, nunca el encabezado
`Authorization`.

## Rate limiting: usa el framework

```csharp
public static class OrderIngestRateLimiterPolicy
{
    public const string PolicyName = "OrderIngest";
    private const int SegmentsPerWindow = 4;

    public static RateLimitPartition<string> CreatePartition(RateLimitSettings settings) =>
        RateLimitPartition.GetSlidingWindowLimiter("global", _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = settings.MaxRequests,
            Window = settings.Window,
            SegmentsPerWindow = SegmentsPerWindow,
            QueueLimit = 0,      // rechaza de inmediato en vez de encolar
        });
}
```

Notas:

- Pon la policy en su propia static class, no en línea dentro de `Program.cs`, para que una prueba
  unitaria pueda comprobar la partición sin arrancar un host.
- Particiona por **client id** (el claim `azp`/`appid`) cuando los clientes sean identificables - una
  sola partición global deja que un cliente ruidoso consuma el presupuesto de los demás. Una partición
  global solo está bien cuando la API tiene exactamente un llamador.
- Una partición basada en claims **no se puede construir antes de la autenticación**. Antes de que
  corra `UseAuthentication()`, `HttpContext.User` no tiene claims, así que todas las peticiones caen en
  la misma partición y el presupuesto por cliente se degrada en silencio a uno global.
- `QueueLimit = 0` rechaza; un límite de cola positivo convierte la sobrecarga en latencia para todos.
- Responde siempre `429` con `Retry-After`. Es un requisito del protocolo y los clientes dependen de
  él.
- Dale a las consultas costosas a terceros su **propia** policy con su propio presupuesto, separada de
  las que sí usan caché, para que un barrido del catálogo no se coma el presupuesto de las consultas
  en vivo.
- El rate limiting de aquí es **por instancia**, no distribuido. Dilo en la documentación; un
  despliegue escalado horizontalmente multiplica cada límite por el número de instancias.

### Dónde va `UseRateLimiter()`

**Exactamente una vez.** No una vez por cada límite que quieras imponer. Cada llamada agrega otra
instancia de `RateLimitingMiddleware`, y cada instancia aplica por su cuenta **tanto** el
`GlobalLimiter` como la policy propia del endpoint. Así que dos llamadas cobran el limitador global dos
veces por petición — el techo real es la mitad de lo que configuraste — *y* evalúan la policy basada en
claims dos veces, la primera antes de la autenticación, cuando no hay claims por los que particionar y
todos los llamadores caen en un mismo cubo.

| Configuración | Posición de la única llamada a `UseRateLimiter()` |
|---|---|
| **Cualquier** policy particionada por un claim (`azp`/`appid`) | **Después** de `UseAuthentication()`, antes de `UseAuthorization()` - la clave de partición es un claim, y los claims solo existen después de que la autenticación haya corrido |
| Todas las particiones globales o por IP | Puede ir **antes** de `UseAuthentication()` |

La regla va sobre la *configuración*, no sobre policies sueltas: una sola policy basada en claims en
cualquier parte del service basta para empujar la única llamada después de la autenticación.

### Dos presupuestos, una llamada

No hace falta un segundo middleware para un segundo presupuesto. Configura los dos niveles en los mismos
`RateLimiterOptions` y deja que el único middleware cobre ambos. `settings` más abajo es tu propia clase
de options, no el `RateLimiterOptions` del framework:

```csharp
builder.Services.AddRateLimiter(options =>
{
    // Nivel 1: toda petición, por IP.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext => RateLimitPartition.GetSlidingWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = settings.MaxRequestsPerIp,
                Window = settings.Window,
                QueueLimit = 0,
            }));

    // Nivel 2: solo en los endpoints que lo declaran, por el claim del client id.
    options.AddPolicy(OrderIngestRateLimiterPolicy.PolicyName,
        _ => OrderIngestRateLimiterPolicy.CreatePartition(settings));
});
```

```csharp
app.UseAuthentication();
app.UseRateLimiter();          // la única llamada: cobra el nivel global por IP y la policy por cliente
app.UseAuthorization();
```

La policy del endpoint aplica solo donde un endpoint la declara, así que el techo por IP es la red de
seguridad para todo y el presupuesto por cliente es la garantía de equidad entre llamadores
autenticados.

### Las avalanchas antes de validar el token son trabajo del borde

Rechazar una avalancha volumétrica *antes* de que llegue a la validación del token es trabajo de un WAF
/ API gateway / front door, no de un segundo `UseRateLimiter()`. Gana menos de lo que parece: la
validación de JWT no hace I/O por petición, porque las claves de firma OIDC se traen una vez y se cachean.
Una avalancha que vale la pena rechazar a esa profundidad vale la pena rechazarla en el borde, donde no
te cuesta nada.

## CORS

**No añadas CORS a una API de servidor a servidor.** Es un mecanismo del navegador; un llamador de
servidor a servidor no lo lee. Una policy de CORS añadida «solo para pruebas locales» sobrevive a
producción y luego se convierte en silencio en lo que un revisor ve como la postura de seguridad de
la API. Prueba las rutas de servidor a servidor con `WebApplicationFactory`, que no necesita CORS.

La única excepción: el navegador es un cliente legítimo, y en ese caso añade una policy con nombre y
una lista explícita de orígenes, nunca la combinación `AllowAnyOrigin` + `AllowCredentials`.

## Anti-patrones

- Un validador de JWT escrito a mano, un `IConfigurationManager<OpenIdConnectConfiguration>` o leer a
  mano el encabezado `Authorization`.
- `SymmetricSecurityKey` con una clave de firma en un `appsettings.json` versionado al repositorio.
- No marcar el endpoint de login con `.AllowAnonymous()` y preguntarse por qué devuelve 401 siempre.
- Un requirement propio que devuelve 401 ante un fallo de autorización — 403 es el código correcto, y
  confundirlos rompe la lógica de reintento del cliente.
- Registrar el token, el encabezado o el payload completo del token en un mensaje de rechazo.
- Un `SlidingWindowRateLimiter` propio cuando existe `RateLimitPartition.GetSlidingWindowLimiter`.
- `429` sin `Retry-After`, o sin un cuerpo ProblemDetails.
- Un único límite global que cubra un endpoint barato y una llamada costosa a un tercero.
- `AddCors(...).AllowAnyOrigin().AllowCredentials()` «para las pruebas».
- Una partición basada en claims con `UseRateLimiter()` colocado antes de `UseAuthentication()` - aún
  no hay claims, así que todos los llamadores comparten una partición y el presupuesto por cliente es
  una ficción.
- Llamar a `UseRateLimiter()` más de una vez para tener dos límites. Cada instancia aplica el
  `GlobalLimiter` *y* la policy del endpoint, así que el presupuesto global se cobra dos veces por
  petición y la policy basada en claims se evalúa una vez antes de la autenticación. Configura los dos
  niveles en unos mismos `RateLimiterOptions`. Ver pitfall 21.
- Un client id o un tenant id escritos a fuego en el código en vez de en la configuración.
