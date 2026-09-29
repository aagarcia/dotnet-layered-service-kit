---
name: layered-minimal-api
description: Build or review a layered .NET 10 Minimal API HTTP service. Use when creating a new .NET service/API from scratch, adding endpoints or a new external integration, building an async ingestion endpoint (validate + enqueue + 202), setting up layers/EF Core/auth/rate limiting/observability/tests, or auditing an existing .NET service against this standard.
---

# Layered .NET 10 Minimal API

## The architecture

Four projects, one dependency direction. Each layer references only downward.

```
┌──────────────────────────────────────────────────────────────┐
│ Api  (Sdk="Microsoft.NET.Sdk.Web")                           │
│   Endpoints/ Filters/ Middlewares/ Security/ RateLimiting/  │
│   Observability/ Services/<Provider>/  Program.cs            │
└───────────────────────────┬──────────────────────────────────┘
                            │ references
┌───────────────────────────▼──────────────────────────────────┐
│ Application.UseCases                                        │
│   Features/ IFeatures/ IServices/                           │
│   Commons/{DTOs,Validations,Mappings,Utils}                  │
└───────────────────────────┬──────────────────────────────────┘
                            │ references
┌───────────────────────────▼──────────────────────────────────┐
│ Infrastructure.Persistence                                   │
│   <DbContext>.cs  Configurations/<Module>/  Contexts/<Module>/ │
│   Batching/  DependencyInjection.cs                          │
└───────────────────────────┬──────────────────────────────────┘
                            │ references
┌───────────────────────────▼──────────────────────────────────┐
│ Domain  (no ProjectReference at all)                         │
│   Entities/ Models/ Enums/ Telemetry/                        │
└──────────────────────────────────────────────────────────────┘
```

Tests mirror the layers under `tests/`, one test project per layer. The solution is a `.slnx` with
virtual folders `/src/` and `/tests/`.

## The core rule

**Every class that opens real I/O (SQL, queue, HTTP, a vendor SDK) is a thin shell. The logic it needs
lives in separate pure classes that unit-test with no network.**

`OrderHandler` (shell: normalizes, calls the port, returns a result) contains no mapping logic —
`OrderNormalizer` does, and it has no dependencies. `CarrierClient` (shell) contains no error
translating — `CarrierErrorTranslator` does, and it is a pure static function over exception types.

If you cannot test a class without a database, a queue or a network, the logic is in the wrong class.
Read `references/02-core-rule-thin-io-shell.md` before writing any new integration.

## Golden rules

1. Each layer references only downward; `Domain` references no project at all.
2. No generic `IRepository<T>`. One `Context` per module, queries hand-written.
3. Every class that opens I/O is a thin shell; its logic lives in a pure, testable class.
4. Use primary constructors in production code, not base classes with constructor overloads.
5. `IValidator<T>` for every request DTO, auto-registered by assembly.
6. A handler returns a result DTO, not an exception; the endpoint maps it to a typed result.
7. Minimal APIs, no controllers. `MapGroup` + a `Map<Module>Endpoints()` extension per module.
8. `TypedResults` everywhere; never `IActionResult`.
9. Body validation via a generic `ValidationFilter<T>` endpoint filter; route params validated separately.
10. `ProblemDetails` for every non-2xx response. `IExceptionHandler` for unhandled exceptions.
11. Native `AddJwtBearer` with `Authority`/`Audience`. Never a hand-written JWT validator.
12. `SetFallbackPolicy(RequireAuthenticatedUser())` — deny by default, `.AllowAnonymous()` per route.
13. Native `AddRateLimiter`, with **exactly one** `UseRateLimiter()` call - two calls double-charge the global limiter and evaluate the claims-based policy before authentication. After `UseAuthentication()` if any policy partitions by `azp`/`appid`; before it if every partition is global or by IP. For two budgets, configure a per-IP `GlobalLimiter` plus a per-client endpoint policy on the same options. 429 with `Retry-After` and ProblemDetails.
14. No CORS in a server-to-server API.
15. Normalize before enqueuing. The queue contract is versioned and documented.
16. Create queues once at startup, never `CreateIfNotExistsAsync` per request.
17. `Channel<T>` bounded with `FullMode = Wait`; `TryWrite` returning `false` is a drop — count it.
18. A shared channel/state wrapper is a **Singleton**, never Scoped.
19. A session-holding SDK is a **single singleton** with its own `SocketsHttpHandler` and a login lock.
20. OpenTelemetry + Azure Monitor exporter, registered only if a connection string exists. No Serilog.
21. `Meter` and `ActivitySource` names are constants in `Domain/Telemetry`, registered explicitly.
22. Typed options sections with `ValidateOnStart`. Secrets never in committed `appsettings.json`.
23. Fixed-offset business time zone, never the OS zone.
24. xUnit v3 on Microsoft.Testing.Platform; EF tests on SQLite in-memory with the connection held open.
25. `dotnet build` with zero warnings before you call it done.

## Workflow

1. **Read the requirement, propose the design before writing code.** State the layers, the module
   folders, the ports you need, the HTTP contract (routes, status codes, payloads), and which
   integrations are push or pull. Get the shape agreed; the code is mechanical after that.
2. **Scaffold with `references/15-checklists.md`.** Four projects, `.slnx`, `Directory.Build.props`,
   `global.json`, `.editorconfig`, the two `DependencyInjection.cs`, the empty `Program.cs`, the
   telemetry class.
3. **Implement bottom-up:** Domain → Persistence → Application.UseCases → Api. Do not start the endpoint
   and work backwards.
4. **Write tests next to the logic you just wrote** — pure logic unit tests, fakes for the ports,
   `WebApplicationFactory` for endpoints and auth.
5. **Write the docs** as you go: `CLAUDE.md`, `docs/ARCHITECTURE.md`, `docs/API-CONTRACT.md`. See
   `references/13-repo-documentation.md`.
6. **Walk `references/14-production-pitfalls.md`** against what you built. Every one of those lessons
   was paid for; check each one explicitly rather than trusting that you avoided them.

## Reference index

Read the file when the situation matches — do not read the whole set.

| Read | When |
|---|---|
| `references/01-layers-and-dependencies.md` | Creating the solution, adding a project, deciding what goes in which layer |
| `references/02-core-rule-thin-io-shell.md` | Writing any class that touches SQL, a queue, HTTP or an SDK — **always** |
| `references/03-domain.md` | Adding a model, an enum, or the telemetry class |
| `references/04-persistence-efcore.md` | `DbContext`, entity configuration, module contexts, bulk writes, deciding whether you need a database at all |
| `references/05-application-usecases.md` | Adding a handler, a DTO, a validator, a mapping, a port to infrastructure |
| `references/06-api-presentation.md` | Adding an endpoint, `Program.cs` ordering, ProblemDetails, OpenAPI, the exception handler |
| `references/07-security-and-rate-limiting.md` | Configuring JWT, a custom authorization requirement, rate limits, the 401 vs 403 split |
| `references/08-async-ingestion-202.md` | Building a receive → validate → normalize → enqueue → 202 endpoint, or non-blocking audit logging |
| `references/09-third-party-sdks.md` | Integrating a vendor SDK: session handling, error translation, in-memory indexes, pagination, decompiling the SDK |
| `references/10-observability.md` | OpenTelemetry wiring, custom meters and activity sources, counters, health checks |
| `references/11-configuration.md` | Options pattern, user secrets, local queue emulator, secret hygiene, time zone |
| `references/12-testing-and-quality.md` | Test project setup, fakes, SQLite in-memory, coverage, analyzer configuration, build warnings |
| `references/13-repo-documentation.md` | Writing or reviewing the repo's docs |
| `references/14-production-pitfalls.md` | **Before merging, and whenever something behaves differently in production than in tests** |
| `references/15-checklists.md` | Following a task end to end: new service, new endpoint, new integration, pre-merge |
