# 11 — Configuración

## La regla

Secciones tipadas con el patrón de options y `ValidateOnStart`. Los secretos nunca en un
`appsettings.json` versionado. El desarrollo local usa `dotnet user-secrets` y un emulador de cola local.
La zona horaria de negocio es un **offset fijo**, nunca la del sistema operativo.

Por qué `ValidateOnStart`: sin él, un ajuste ausente o mal formado lo descubre la primera petición que lo
necesita — en producción, a las 3 de la mañana, como un 500. Con él, el servicio se niega a arrancar con
un mensaje que nombra el ajuste, sobre el que una plataforma puede actuar de inmediato.

## Opciones tipadas

```csharp
namespace Contoso.Orders.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>Client id of the expected app registration (claims "azp"/"appid"). Optional.</summary>
    public string? RequiredClientId { get; set; }
}
```

```csharp
// Program.cs
builder.Services.AddOptions<SqlOptions>()
    .Bind(builder.Configuration.GetSection(SqlOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Sql:ConnectionString is required.")
    .ValidateOnStart();
```

Mantén una clase de options por integración, con una constante `SectionName` usada tanto para el enlace
como para la lectura. Nunca indexes la configuración con `configuration["Section:Key"]` disperso en varios
archivos: una errata se convierte en un `null` que fluye hasta un constructor de cliente.

`SqlOptions` es la única excepción que vale la pena señalar: se enlaza dentro del callback de
`AddDbContextFactory` a través de `IOptions<SqlOptions>`, de modo que el valor se resuelve desde el
contenedor ya construido y no queda capturado en el momento del registro.

## Archivos appsettings

```
appsettings.json                  versionado: estructura, valores por defecto no secretos, secretos vacíos
appsettings.Development.json      NO versionado (en gitignore) - sobrescrituras locales
appsettings.QA.json               estructura versionada, secretos inyectados por la plataforma
appsettings.Production.json       estructura versionada, secretos inyectados por la plataforma
```

Archivo versionado con los secretos vacíos, para que la forma sea visible y revisable:

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Sql": { "ConnectionString": "" },
  "Queue": { "ConnectionString": "", "OrdersQueueName": "orders-queue" },
  "Jwt": { "Authority": "", "Audience": "", "RequiredClientId": "" },
  "Carrier": { "Server": "", "Database": "", "UserName": "", "Password": "", "DirectoryMinutes": 15 },
  "RateLimit": { "MaxRequests": 1000, "Window": "00:01:00" },
  "OpenApi": { "Enabled": false },
  "ApplicationInsights": { "ConnectionString": "" }
}
```

`OpenApi.Enabled` es `false` en el archivo versionado. Activar la superficie de la API es una decisión del
entorno, no un valor por defecto.

## Desarrollo local

```bash
cd src/Contoso.Orders.Api
dotnet user-secrets init
dotnet user-secrets set "Sql:ConnectionString" "Server=localhost;Database=orders;..."
dotnet user-secrets set "Queue:ConnectionString" "UseDevelopmentStorage=true"
dotnet user-secrets set "Jwt:Authority" "https://login.example.com/<tenant>/v2.0"
dotnet user-secrets set "Jwt:Audience" "api://contoso-orders"
```

`UseDevelopmentStorage=true` es la cadena de conexión conocida del emulador de almacenamiento local, así
que el código de cola corre sin cambios contra una instancia local. La cadena de conexión equivalente
para la base de datos es la que espera tu motor local; consulta la documentación del emulador en lugar de
adivinar.

`appsettings.Development.json` va en `.gitignore`. Un desarrollador que pone ahí una cadena de conexión
real y la versiona acaba de filtrar una credencial, y el archivo está versionado por defecto en la mayoría
de las plantillas — así que la regla de ignorado forma parte del andamiaje, no es un trámite tardío.

## Higiene de secretos

- Nada secreto en un `appsettings*.json` versionado: ni cadenas de conexión, ni claves de firma, ni
  contraseñas, ni client secrets, ni URLs específicas de un tenant.
- En la nube: variables de entorno inyectadas por la plataforma o un almacén de secretos con identidad
  administrada.
- En local: `dotnet user-secrets`.
- Un secreto que se ha versionado está comprometido. Rótalo; quitar la línea no deshace la filtración.
- Usa autenticación de **Microsoft Entra ID** para la base de datos en lugar de un par usuario/contraseña,
  para que rotar una contraseña nunca sea una tarea de despliegue.
- `.gitignore` también cubre `*.user`, `*.log`, `.env`, `secrets.json`.

## Zona horaria de negocio

```csharp
public static class BusinessTime
{
    // Offset fijo para la zona horaria de negocio (p. ej. UTC-05:00). Nunca la zona del sistema
    // operativo, y nunca un id de zona consciente del horario de verano: el offset es una regla de
    // negocio, no una propiedad del host.
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public static DateTimeOffset ParseLocal(string value, string format) =>
        new(DateTime.ParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None), Offset);

    public static DateTimeOffset ToBusiness(DateTimeOffset instant) =>
        instant.ToOffset(Offset).AddTicks(-(instant.ToOffset(Offset).Ticks % TimeSpan.TicksPerSecond));
}
```

Por qué esto merece su propia sección: parsear con la zona del servidor hace que la misma entrada produzca
un instante distinto en el portátil de un desarrollador y en la región de producción, y solo aparece como
una discrepancia de una hora en un informe. El offset es un hecho de negocio; escríbelo como constante
con un comentario que explique de dónde sale.

Almacena y transmite las marcas de tiempo en UTC; convierte solo para mostrar, en los bordes.

## Nomenclatura

Los tipos siempre en inglés. Las propiedades pueden seguir el idioma del sistema de origen o el de la base
de datos — **elige uno por repositorio y anótalo en `CLAUDE.md`**. Un modelo medio traducido es peor que
cualquiera de los dos, porque nadie puede buscarlo. Elijas el que elijas, los nombres de las rutas HTTP
siguen en inglés: son un contrato público con clientes que no controlas.

## Anti-patrones

- Secretos en un `appsettings*.json` versionado, incluidos los «solo de desarrollo».
- `configuration["Section:Key"]` leído en archivos dispersos en lugar de opciones tipadas.
- Sin `ValidateOnStart`, así que un ajuste ausente aflora como un 500 en el momento de la petición.
- `appsettings.Development.json` sin meter en gitignore.
- `DateTime.Now` / `DateTime.Parse` con la zona del sistema operativo para una marca de tiempo de negocio.
- `TimeZoneInfo.FindSystemTimeZoneById` para un offset de negocio — un cambio en la regla del horario de
  verano mueve datos históricos en silencio.
- Una cadena de conexión de usuario/contraseña donde hay identidad administrada disponible.
- `builder.Configuration.GetConnectionString(...)` en línea en `Program.cs` sin validación de options.
- Guardar en la base de datos horas locales de negocio en lugar de UTC.
- Idiomas de nomenclatura distintos para el mismo concepto entre capas.
