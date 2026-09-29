# 15 — Listas de verificación

## (a) Crear un servicio desde cero

Diseña primero, por escrito, y consíguelo acordado: capas, carpetas de módulo, puertos, rutas, códigos de estado, payloads y si cada integración es push o pull.

```bash
mkdir Contoso.Orders && cd Contoso.Orders

dotnet new sln -n Contoso.Orders
dotnet new classlib -n Contoso.Orders.Domain                     -o src/Contoso.Orders.Domain
dotnet new classlib -n Contoso.Orders.Infrastructure.Persistence -o src/Contoso.Orders.Infrastructure.Persistence
dotnet new classlib -n Contoso.Orders.Application.UseCases      -o src/Contoso.Orders.Application.UseCases
dotnet new web      -n Contoso.Orders.Api                       -o src/Contoso.Orders.Api

# Los tests reflejan las capas
dotnet new xunit -n Contoso.Orders.Infrastructure.Persistence.Tests -o tests/Contoso.Orders.Infrastructure.Persistence.Tests
dotnet new xunit -n Contoso.Orders.Application.UseCases.Tests      -o tests/Contoso.Orders.Application.UseCases.Tests
dotnet new xunit -n Contoso.Orders.Api.Tests                       -o tests/Contoso.Orders.Api.Tests
```

Grafo de referencias — solo hacia abajo:

```bash
dotnet add src/Contoso.Orders.Infrastructure.Persistence reference src/Contoso.Orders.Domain
dotnet add src/Contoso.Orders.Application.UseCases      reference src/Contoso.Orders.Infrastructure.Persistence
dotnet add src/Contoso.Orders.Api                       reference src/Contoso.Orders.Application.UseCases
dotnet add src/Contoso.Orders.Api                       reference src/Contoso.Orders.Infrastructure.Persistence

dotnet add tests/Contoso.Orders.Infrastructure.Persistence.Tests reference src/Contoso.Orders.Infrastructure.Persistence
dotnet add tests/Contoso.Orders.Application.UseCases.Tests      reference src/Contoso.Orders.Application.UseCases
dotnet add tests/Contoso.Orders.Api.Tests                       reference src/Contoso.Orders.Api
```

`dotnet sln add` escribe los GUID en un `.sln` clásico. Para un `.slnx`, escribe el XML a mano — son cuatro elementos (ver `references/01-layers-and-dependencies.md`).

## (a2) Paquetes

Una librería de clases arranca **sin ninguna** referencia de framework, y el Web SDK solo pre-importa
los namespaces de ASP.NET. Sin este paso la solución no compila, y los errores nombran tipos
(`BackgroundService`, `IQueueService`, `AddMapster`, `AddOpenApi`) en lugar del paquete que realmente
falta. Este conjunto exacto es contra el que se verificaron las plantillas:

```bash
# Persistencia — los tres que muerden no son de EF
dotnet add src/Contoso.Orders.Infrastructure.Persistence package Microsoft.EntityFrameworkCore.SqlServer
dotnet add src/Contoso.Orders.Infrastructure.Persistence package Microsoft.Extensions.Hosting.Abstractions
dotnet add src/Contoso.Orders.Infrastructure.Persistence package Microsoft.Extensions.Options.ConfigurationExtensions

# Application
dotnet add src/Contoso.Orders.Application.UseCases package FluentValidation
dotnet add src/Contoso.Orders.Application.UseCases package FluentValidation.DependencyInjectionExtensions   # AddValidatorsFromAssembly
dotnet add src/Contoso.Orders.Application.UseCases package Mapster
dotnet add src/Contoso.Orders.Application.UseCases package Mapster.DependencyInjection                    # AddMapster

# Api
dotnet add src/Contoso.Orders.Api package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add src/Contoso.Orders.Api package Microsoft.AspNetCore.OpenApi                                  # AddOpenApi / MapOpenApi
dotnet add src/Contoso.Orders.Api package Azure.Monitor.OpenTelemetry.AspNetCore
dotnet add src/Contoso.Orders.Api package OpenTelemetry.Extensions.Hosting
dotnet add src/Contoso.Orders.Api package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/Contoso.Orders.Api package OpenTelemetry.Instrumentation.Http
dotnet add src/Contoso.Orders.Api package OpenTelemetry.Instrumentation.SqlClient                        # AddSqlClientInstrumentation
dotnet add src/Contoso.Orders.Api package OpenTelemetry.Instrumentation.Runtime
dotnet add src/Contoso.Orders.Api package Scalar.AspNetCore
dotnet add src/Contoso.Orders.Api package Azure.Storage.Queues                                   # solo con el patrón de ingesta asíncrona sobre Azure Queue Storage (references/08)

# Tests
dotnet add tests/Contoso.Orders.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
```

Dos trampas que vale la pena nombrar, porque las dos parecen un `using` que falta y no lo son:

- `Mapster` y `Mapster.DependencyInjection` son **paquetes distintos**. El segundo es el dueño de
  `AddMapster()`.
- `FluentValidation` y `FluentValidation.DependencyInjectionExtensions` igual: el segundo es el dueño
  de `AddValidatorsFromAssembly`.

`Contoso.Orders.Domain` no necesita ninguno. Si tu service tiene una cola o un SDK de terceros, es un
paquete más en `Contoso.Orders.Api` — ver `references/09-third-party-sdks.md`.

Luego, en este orden:

- [ ] `Directory.Build.props`: LangVersion 14, Nullable, ImplicitUsings, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile`, `NoWarn CS1591`, paquete del analizador.
- [ ] `global.json` con el runner de pruebas de la plataforma.
- [ ] `.editorconfig` con IDE0005 como warning.
- [ ] `.gitignore` que incluya `appsettings.Development.json`, `launchSettings.json`, `*.user`, `*.log`, `.env`, `secrets.json`.
- [ ] `Domain/Telemetry/OrderTelemetry.cs` — la constante `SourceName`, `ActivitySource`, `Meter`.
- [ ] `Infrastructure.Persistence`: el `DbContext` vacío con `ApplyConfigurationsFromAssembly`, `SqlOptions`, `DependencyInjection.AddPersistence`.
- [ ] `Application.UseCases`: `OperationResultDto`, `MapsterGlobalConfig`, `DependencyInjection.AddApplicationServices`.
- [ ] `Api`: `ObservabilityExtensions.AddObservability`, `ValidationFilter<T>`, `HandlingHelper`, `GlobalExceptionHandler`, `Program.cs` vacío en el orden documentado, `HealthEndpoints`.
- [ ] `appsettings.json` con los secretos vacíos y `OpenApi:Enabled: false`.
- [ ] `public partial class Program;` al final de `Program.cs`, para `WebApplicationFactory<Program>`.
- [ ] `UserSecretsId` en el proyecto de API (`dotnet user-secrets init`).
- [ ] `CLAUDE.md` y los cuatro archivos de `docs/`, empezando por `templates/CLAUDE.md.template`.
- [ ] Primer módulo real, de principio a fin, de abajo hacia arriba.
- [ ] `dotnet build` con cero advertencias, `dotnet test` en verde.

## (b) Añadir un endpoint

- [ ] DTO de request en `Commons/DTOs`, DTO de respuesta si no se reutiliza una forma existente.
- [ ] Validador en `Commons/Validations` — se auto-registra por ensamblado; no añadas una línea de registro.
- [ ] Configuración de Mapster en `Commons/Mappings` si el mapeo no sigue la convención de nombres.
- [ ] Interfaz del handler en `IFeatures/`, implementación en `Features/` (plano — sin subcarpeta nueva).
- [ ] El handler devuelve un `OperationResultDto` / `OperationResultDto<T>`, no una excepción.
- [ ] Toda lógica que podría ser una prueba unitaria está en una clase pura, no en el handler.
- [ ] Ruta en `Endpoints/<Module>Endpoints.cs` dentro del `Map<Module>Endpoints()` existente.
- [ ] `.AddEndpointFilter<ValidationFilter<TRequest>>()` en cada endpoint con body.
- [ ] `.RequireAuthorization(...)` solo si necesita más que la policy de respaldo; si no, el valor por defecto basta.
- [ ] `.RequireRateLimiting(...)` en todo lo que cueste una llamada saliente.
- [ ] Valores nuevos de `code` añadidos a `HandlingHelper.ToHttpResult` y a `docs/API-CONTRACT.md`.
- [ ] Auditoría/telemetría: una activity, un contador con un tag `reason`, una fila de auditoría si el estándar lo exige.
- [ ] Pruebas: pruebas unitarias de la lógica pura, una prueba con `WebApplicationFactory` para la ruta feliz más 401/403/429/400 según aplique.
- [ ] `dotnet build`, `dotnet test` y un recorrido por `14-production-pitfalls.md`.

## (c) Añadir una integración con un tercero

Diseña primero a partir de la naturaleza del proveedor. Un receptor push, un catálogo por polling y una llamada REST stateless quieren tres diseños distintos. Reutiliza la estructura y las convenciones; no reutilices el flujo.

- [ ] Lee los límites reales y la semántica de errores del proveedor. Descompila el SDK (`ilspycmd`) — no diseñes a partir de su README.
- [ ] Interfaz del puerto en `Application.UseCases/IServices/`, estrecha: solo las operaciones que los handlers llaman de verdad.
- [ ] Carpeta `Api/Services/<Provider>/` — el único lugar donde se referencia el paquete.
- [ ] Clase de options + constante `SectionName`, con `ValidateOnStart`.
- [ ] Registro en un único método de extensión `Add<Provider>()`.
- [ ] Shell: la clase cliente sostiene la conexión y no hace **ninguna** lógica.
- [ ] Traductor de errores puro que devuelve un enum de errores pequeño, y `null` para «no es un error del proveedor», para que tus propios bugs sigan aflorando.
- [ ] Manejo de sesión: singleton, su propio `SocketsHttpHandler`, `SemaphoreSlim` en el primer login. AOT/trimming desactivados.
- [ ] Mapper puro de tipos del SDK a tipos de dominio.
- [ ] Lógica pura de indexación/paginación si cacheas; reporta duplicados; orden estable con desempate.
- [ ] Refresco en un `BackgroundService` que nunca lanza y conserva el snapshot anterior ante fallo.
- [ ] Los endpoints responden `503` cuando la integración no está configurada, y el resto de la API sigue funcionando.
- [ ] Policy propia de rate limiting con su propio presupuesto, separada de los endpoints baratos.
- [ ] Telemetría: una activity por operación, contadores con un tag `reason`, sin tags de alta cardinalidad.
- [ ] Pruebas unitarias del traductor, el mapper, el índice y la paginación — todas con objetos del SDK en memoria, sin red.
- [ ] Una prueba de rate limiting y de traducción de errores a través de `WebApplicationFactory`.
- [ ] `docs/ARCHITECTURE.md` actualizado: el nuevo componente, la ruta feliz, la ruta de error, la decisión y su por qué.
- [ ] `docs/API-CONTRACT.md` actualizado con las nuevas rutas y códigos de estado.

## (d) Antes de integrar

- [ ] `dotnet build` — **cero advertencias**. Las directivas using sin usar se corrigen con `dotnet format style --diagnostics IDE0005`.
- [ ] `dotnet test` en verde; el comportamiento nuevo tiene pruebas; la lógica pura tiene pruebas unitarias, no solo de integración.
- [ ] La ejecución de cobertura produce un archivo (no solo el código de salida 0).
- [ ] `references/14-production-pitfalls.md` recorrido, punto por punto, contra este cambio.
- [ ] Todo lo que SQLite no puede demostrar (lotes, transacciones, estrategia de ejecución, precisión, SQL crudo) verificado contra el motor real.
- [ ] Ningún secreto en ningún `appsettings*.json` versionado; `appsettings.Development.json` está en gitignore.
- [ ] `grep -ri "AddCors\|UseCors" src/` no devuelve nada en una API servidor-a-servidor.
- [ ] Toda respuesta no-2xx es un `ProblemDetails`; el 429 lleva `Retry-After`; 401 vs 403 se distinguen.
- [ ] `UseRateLimiter()` se llama **exactamente una vez**, después de `UseAuthentication()` si alguna policy particiona por un claim; dos llamadas cobran dos veces el limitador global y corren la policy por claims sin autenticar (pitfall 21).
- [ ] Ningún `Task.Run` nuevo en un camino de petición; toda llamada async se espera.
- [ ] Las referencias entre capas siguen apuntando solo hacia abajo; ningún `ProjectReference` hacia arriba nuevo.
- [ ] Documentación actualizada **en este cambio**: decisiones de `CLAUDE.md`, flujos de `ARCHITECTURE.md`, contrato de `API-CONTRACT.md`, `TROUBLESHOOTING.md` si se depuró algo nuevo.
- [ ] Toda decisión no obvia tiene un *por qué* registrado y su consecuencia si se olvida.
- [ ] No se modificó nada fuera del alcance de este cambio.
