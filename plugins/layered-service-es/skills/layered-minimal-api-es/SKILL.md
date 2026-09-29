---
name: layered-minimal-api-es
description: Crear o revisar un service HTTP .NET 10 Minimal API en capas. Úsala cuando haya que crear un service/API .NET desde cero, agregar endpoints o una integración de terceros nueva, construir un endpoint de ingesta asíncrona (validar + encolar + 202), configurar capas/EF Core/auth/rate limiting/observabilidad/tests, o auditar un service .NET existente contra este estándar.
---

# Service .NET 10 Minimal API en capas

## La arquitectura

Cuatro proyectos, una sola dirección de dependencias. Cada capa referencia únicamente hacia abajo.

```
┌──────────────────────────────────────────────────────────────┐
│ Api  (Sdk="Microsoft.NET.Sdk.Web")                           │
│   Endpoints/ Filters/ Middlewares/ Security/ RateLimiting/  │
│   Observability/ Services/<Provider>/  Program.cs            │
└───────────────────────────┬──────────────────────────────────┘
                            │ referencia
┌───────────────────────────▼──────────────────────────────────┐
│ Application.UseCases                                        │
│   Features/ IFeatures/ IServices/                           │
│   Commons/{DTOs,Validations,Mappings,Utils}                  │
└───────────────────────────┬──────────────────────────────────┘
                            │ referencia
┌───────────────────────────▼──────────────────────────────────┐
│ Infrastructure.Persistence                                   │
│   <DbContext>.cs  Configurations/<Module>/  Contexts/<Module>/ │
│   Batching/  DependencyInjection.cs                          │
└───────────────────────────┬──────────────────────────────────┘
                            │ referencia
┌───────────────────────────▼──────────────────────────────────┐
│ Domain  (sin ningún ProjectReference)                        │
│   Entities/ Models/ Enums/ Telemetry/                        │
└──────────────────────────────────────────────────────────────┘
```

Los tests reflejan las capas bajo `tests/`, un proyecto de test por capa. La solución es un `.slnx` con
carpetas virtuales `/src/` y `/tests/`.

## La regla central

**Toda clase que abre I/O real (SQL, cola, HTTP, un SDK de proveedor) es un shell delgado. La lógica
que necesita vive en clases puras separadas que se testean por unidad sin red.**

`OrderHandler` (shell: normaliza, llama al puerto, devuelve un resultado) no contiene lógica de mapeo
— esa la hace `OrderNormalizer`, que no tiene dependencias. `CarrierClient` (shell) no traduce errores
— eso lo hace `CarrierErrorTranslator`, una función estática pura sobre tipos de excepción.

Si no puedes testear una clase sin base de datos, sin cola o sin red, la lógica está en la clase
equivocada. Lee `references/02-core-rule-thin-io-shell.md` antes de escribir cualquier integración
nueva.

## Reglas de oro

1. Cada capa referencia solo hacia abajo; `Domain` no referencia ningún proyecto.
2. Sin `IRepository<T>` genérico. Un `Context` por módulo, con las queries escritas a mano.
3. Toda clase que abre I/O es un shell delgado; su lógica vive en una clase pura y testeable.
4. Primary constructors en el código de producción, no clases base con sobrecargas de constructor.
5. `IValidator<T>` para cada DTO de request, registrado automáticamente por ensamblado.
6. Un handler devuelve un DTO de resultado, no una excepción; el endpoint lo mapea a un typed result.
7. Minimal APIs, sin controllers. `MapGroup` + una extensión `Map<Module>Endpoints()` por módulo.
8. `TypedResults` siempre; nunca `IActionResult`.
9. Validación de body con un endpoint filter genérico `ValidationFilter<T>`; los route params se
   validan aparte.
10. `ProblemDetails` en toda respuesta no-2xx. `IExceptionHandler` para las excepciones no controladas.
11. `AddJwtBearer` nativo con `Authority`/`Audience`. Nunca un validador JWT propio.
12. `SetFallbackPolicy(RequireAuthenticatedUser())` — denegar por defecto, `.AllowAnonymous()` por ruta.
13. `AddRateLimiter` nativo, con **exactamente una** llamada a `UseRateLimiter()` - dos llamadas cobran dos veces el limitador global y evalúan la policy por claims antes de la autenticación. Después de `UseAuthentication()` si alguna policy particiona por `azp`/`appid`; antes si todas las particiones son globales o por IP. Para tener dos presupuestos, configura un `GlobalLimiter` por IP más una policy de endpoint por cliente en los mismos options. 429 con `Retry-After` y ProblemDetails.
14. Sin CORS en una API servidor-a-servidor.
15. Normalizar antes de encolar. El contrato de cola está versionado y documentado.
16. Crear las colas una vez al arrancar, nunca `CreateIfNotExistsAsync` por request.
17. `Channel<T>` acotado con `FullMode = Wait`; un `TryWrite` que devuelve `false` es un descarte —
    cuéntalo.
18. Un wrapper sobre un canal o estado compartido es **Singleton**, nunca Scoped.
19. Un SDK que mantiene sesión es un **singleton único** con su propio `SocketsHttpHandler` y un lock de
    login.
20. OpenTelemetry + exporter de Azure Monitor, registrado solo si hay connection string. Sin Serilog.
21. Los nombres de `Meter` y `ActivitySource` son constantes en `Domain/Telemetry`, registrados
    explícitamente.
22. Secciones tipadas con Options pattern y `ValidateOnStart`. Secretos nunca en `appsettings.json`
    commiteado.
23. Zona horaria del negocio con offset fijo, nunca la del sistema operativo.
24. xUnit v3 sobre Microsoft.Testing.Platform; tests de EF sobre SQLite in-memory con la conexión
    abierta.
25. `dotnet build` con cero warnings antes de decir que está terminado.

## Flujo de trabajo

1. **Leer el requerimiento y proponer el diseño antes de escribir código.** Define las capas, las
   carpetas de los módulos, los puertos que hacen falta, el contrato HTTP (rutas, códigos de estado,
   payloads) y qué integraciones son push y cuáles pull. Acuerda la forma; después el código es
   mecánico.
2. **Andamiaje con `references/15-checklists.md`.** Cuatro proyectos, `.slnx`,
   `Directory.Build.props`, `global.json`, `.editorconfig`, los dos `DependencyInjection.cs`, el
   `Program.cs` vacío, la clase de telemetría.
3. **Implementar de abajo hacia arriba:** Domain → Persistence → Application.UseCases → Api. No
   empieces por el endpoint y trabajes hacia atrás.
4. **Escribir los tests junto a la lógica que acabas de escribir** — lógica pura por unidad, fakes para
   los puertos, `WebApplicationFactory` para endpoints y auth.
5. **Escribir la documentación sobre la marcha:** `CLAUDE.md`, `docs/ARQUITECTURA.md`,
   `docs/API-CONTRATO.md`. Ver `references/13-repo-documentation.md`.
6. **Recorrer `references/14-production-pitfalls.md`** sobre lo que construiste. Cada una de esas
   lecciones costó plata; revísalas una por una en vez de confiar en que las evitaste por instinto.

## Índice de references

Lee el archivo cuando la situación coincida — no leas el conjunto entero.

| Lee | Cuándo |
|---|---|
| `references/01-layers-and-dependencies.md` | Al crear la solución, agregar un proyecto o decidir qué va en cada capa |
| `references/02-core-rule-thin-io-shell.md` | Al escribir cualquier clase que toque SQL, cola, HTTP o un SDK — **siempre** |
| `references/03-domain.md` | Al agregar un modelo, un enum o la clase de telemetría |
| `references/04-persistence-efcore.md` | `DbContext`, configuración de entidades, contextos por módulo, escrituras bulk, o si de verdad necesitas base de datos |
| `references/05-application-usecases.md` | Al agregar un handler, un DTO, un validator, un mapping o un puerto a infraestructura |
| `references/06-api-presentation.md` | Al agregar un endpoint, el orden de `Program.cs`, ProblemDetails, OpenAPI, el manejador de excepciones |
| `references/07-security-and-rate-limiting.md` | Al configurar JWT, un requirement de autorización propio, rate limits, o la separación 401 vs 403 |
| `references/08-async-ingestion-202.md` | Al construir un endpoint recibir → validar → normalizar → encolar → 202, o al auditar sin bloquear |
| `references/09-third-party-sdks.md` | Al integrar un SDK de proveedor: sesión, traducción de errores, índices en memoria, paginación, descompilar el SDK |
| `references/10-observability.md` | Cableado de OpenTelemetry, meters y activity sources propios, contadores, health checks |
| `references/11-configuration.md` | Options pattern, user secrets, emulador local de colas, higiene de secretos, zona horaria |
| `references/12-testing-and-quality.md` | Setup de proyectos de test, fakes, SQLite in-memory, cobertura, configuración de analizadores, warnings de build |
| `references/13-repo-documentation.md` | Al escribir o revisar la documentación del repo |
| `references/14-production-pitfalls.md` | **Antes de mergear, y siempre que algo se comporte distinto en producción que en los tests** |
| `references/15-checklists.md` | Para seguir una tarea de punta a punta: service nuevo, endpoint nuevo, integración nueva, pre-merge |
