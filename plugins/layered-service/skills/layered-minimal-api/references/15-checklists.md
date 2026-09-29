# 15 — Checklists

## (a) Create a service from scratch

Design first, in writing, and get it agreed: layers, module folders, ports, routes, status codes,
payloads, and whether each integration is push or pull.

```bash
mkdir Contoso.Orders && cd Contoso.Orders

dotnet new sln -n Contoso.Orders
dotnet new classlib -n Contoso.Orders.Domain                     -o src/Contoso.Orders.Domain
dotnet new classlib -n Contoso.Orders.Infrastructure.Persistence -o src/Contoso.Orders.Infrastructure.Persistence
dotnet new classlib -n Contoso.Orders.Application.UseCases      -o src/Contoso.Orders.Application.UseCases
dotnet new web      -n Contoso.Orders.Api                       -o src/Contoso.Orders.Api

# Tests mirror the layers
dotnet new xunit -n Contoso.Orders.Infrastructure.Persistence.Tests -o tests/Contoso.Orders.Infrastructure.Persistence.Tests
dotnet new xunit -n Contoso.Orders.Application.UseCases.Tests      -o tests/Contoso.Orders.Application.UseCases.Tests
dotnet new xunit -n Contoso.Orders.Api.Tests                       -o tests/Contoso.Orders.Api.Tests
```

Reference graph — downward only:

```bash
dotnet add src/Contoso.Orders.Infrastructure.Persistence reference src/Contoso.Orders.Domain
dotnet add src/Contoso.Orders.Application.UseCases      reference src/Contoso.Orders.Infrastructure.Persistence
dotnet add src/Contoso.Orders.Api                       reference src/Contoso.Orders.Application.UseCases
dotnet add src/Contoso.Orders.Api                       reference src/Contoso.Orders.Infrastructure.Persistence

dotnet add tests/Contoso.Orders.Infrastructure.Persistence.Tests reference src/Contoso.Orders.Infrastructure.Persistence
dotnet add tests/Contoso.Orders.Application.UseCases.Tests      reference src/Contoso.Orders.Application.UseCases
dotnet add tests/Contoso.Orders.Api.Tests                       reference src/Contoso.Orders.Api
```

`dotnet sln add` writes the GUIDs into a classic `.sln`. For a `.slnx`, write the XML by hand — it is
four elements (see `references/01-layers-and-dependencies.md`).

## (a2) Packages

A class library starts with **no** framework references, and the Web SDK only pre-imports ASP.NET
namespaces. Without this step the solution does not compile, and the errors name types
(`BackgroundService`, `IQueueService`, `AddMapster`, `AddOpenApi`) rather than the missing package
that actually caused them. This exact set is what the templates were verified against:

```bash
# Persistence — the three that bite are not EF's
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
dotnet add src/Contoso.Orders.Api package Azure.Storage.Queues                                   # only with the async-ingestion pattern on Azure Queue Storage (references/08)

# Tests
dotnet add tests/Contoso.Orders.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
```

Two traps worth naming, because both look like a missing `using` and are not:

- `Mapster` and `Mapster.DependencyInjection` are **different packages**. The second one owns
  `AddMapster()`.
- `FluentValidation` and `FluentValidation.DependencyInjectionExtensions` likewise: the second owns
  `AddValidatorsFromAssembly`.

`Contoso.Orders.Domain` needs none. If your service has a queue or a third-party SDK, that is one more
package on `Contoso.Orders.Api` — see `references/09-third-party-sdks.md`.

Then, in this order:

- [ ] `Directory.Build.props`: LangVersion 14, Nullable, ImplicitUsings, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile`, `NoWarn CS1591`, analyzer package.
- [ ] `global.json` with the platform test runner.
- [ ] `.editorconfig` with IDE0005 as a warning.
- [ ] `.gitignore` including `appsettings.Development.json`, `launchSettings.json`, `*.user`, `*.log`, `.env`, `secrets.json`.
- [ ] `Domain/Telemetry/OrderTelemetry.cs` — the `SourceName` constant, `ActivitySource`, `Meter`.
- [ ] `Infrastructure.Persistence`: the empty `DbContext` with `ApplyConfigurationsFromAssembly`, `SqlOptions`, `DependencyInjection.AddPersistence`.
- [ ] `Application.UseCases`: `OperationResultDto`, `MapsterGlobalConfig`, `DependencyInjection.AddApplicationServices`.
- [ ] `Api`: `ObservabilityExtensions.AddObservability`, `ValidationFilter<T>`, `HandlingHelper`, `GlobalExceptionHandler`, empty `Program.cs` in the documented order, `HealthEndpoints`.
- [ ] `appsettings.json` with empty secrets and `OpenApi:Enabled: false`.
- [ ] `public partial class Program;` at the end of `Program.cs`, for `WebApplicationFactory<Program>`.
- [ ] `UserSecretsId` on the API project (`dotnet user-secrets init`).
- [ ] `CLAUDE.md` and the four `docs/` files, starting from `templates/CLAUDE.md.template`.
- [ ] First real module, end to end, bottom-up.
- [ ] `dotnet build` with zero warnings, `dotnet test` green.

## (b) Add an endpoint

- [ ] Request DTO in `Commons/DTOs`, response DTO if not reusing an existing shape.
- [ ] Validator in `Commons/Validations` — it auto-registers by assembly; do not add a registration line.
- [ ] Mapster config in `Commons/Mappings` if the mapping is not name-convention.
- [ ] Handler interface in `IFeatures/`, implementation in `Features/` (flat — no new subfolder).
- [ ] Handler returns an `OperationResultDto` / `OperationResultDto<T>`, not an exception.
- [ ] Any logic that could be a unit test is in a pure class, not in the handler.
- [ ] Route in `Endpoints/<Module>Endpoints.cs` inside the existing `Map<Module>Endpoints()`.
- [ ] `.AddEndpointFilter<ValidationFilter<TRequest>>()` on every endpoint with a body.
- [ ] `.RequireAuthorization(...)` only if it needs more than the fallback policy; otherwise the default is enough.
- [ ] `.RequireRateLimiting(...)` on anything that costs an outbound call.
- [ ] New `code` values added to `HandlingHelper.ToHttpResult` and to `docs/API-CONTRACT.md`.
- [ ] Audit/telemetry: an activity, a counter with a `reason` tag, an audit row if the standard requires it.
- [ ] Tests: unit tests for the pure logic, one `WebApplicationFactory` test for the happy path plus 401/403/429/400 as applicable.
- [ ] `dotnet build`, `dotnet test`, and a walk of `14-production-pitfalls.md`.

## (c) Add an external integration

Design from the provider's nature first. A push receiver, a polled catalogue and a stateless REST call
want three different designs. Reuse the structure and the conventions; do not reuse the flow.

- [ ] Read the provider's actual limits and error semantics. Decompile the SDK (`ilspycmd`) — do not design from its README.
- [ ] Port interface in `Application.UseCases/IServices/`, narrow: only the operations handlers actually call.
- [ ] Folder `Api/Services/<Provider>/` — the only place the package is referenced.
- [ ] Options class + `SectionName` constant, with `ValidateOnStart`.
- [ ] Registration in one `Add<Provider>()` extension method.
- [ ] Shell: the client class holds the connection and does **no** logic.
- [ ] Pure error translator returning a small error enum, and `null` for "not a provider error" so your own bugs still surface.
- [ ] Session handling: singleton, own `SocketsHttpHandler`, `SemaphoreSlim` on first login. AOT/trimming off.
- [ ] Pure mapper from SDK types to domain types.
- [ ] Pure index/pagination logic if you cache; report duplicates; stable sort with a tie-break.
- [ ] Refresh in a `BackgroundService` that never throws and keeps the previous snapshot on failure.
- [ ] Endpoints answer `503` when the integration is unconfigured, and the rest of the API keeps working.
- [ ] Own rate-limit policy with its own budget, separate from the cheap endpoints.
- [ ] Telemetry: activity per operation, counters with a `reason` tag, no high-cardinality tags.
- [ ] Unit tests for the translator, the mapper, the index and the pagination — all with SDK objects in
      memory, no network.
- [ ] A rate-limit and error-translation test through `WebApplicationFactory`.
- [ ] `docs/ARCHITECTURE.md` updated: the new component, the happy path, the error path, the decision and its why.
- [ ] `docs/API-CONTRACT.md` updated with the new routes and status codes.

## (d) Before merging

- [ ] `dotnet build` — **zero warnings**. Unused usings fixed with `dotnet format style --diagnostics IDE0005`.
- [ ] `dotnet test` green; new behaviour has tests; the pure logic is unit-tested, not only integration-tested.
- [ ] Coverage run produces a file (not just exit code 0).
- [ ] `references/14-production-pitfalls.md` walked, item by item, against this change.
- [ ] Anything SQLite cannot prove (bulk, transactions, execution strategy, precision, raw SQL) verified against the real engine.
- [ ] No secret in any committed `appsettings*.json`; `appsettings.Development.json` is gitignored.
- [ ] `grep -ri "AddCors\|UseCors" src/` returns nothing in a server-to-server API.
- [ ] Every non-2xx response is a `ProblemDetails`; 429 carries `Retry-After`; 401 vs 403 are distinct.
- [ ] `UseRateLimiter()` is called **exactly once**, after `UseAuthentication()` if any policy partitions by a claim; two calls double-charge the global limiter and run the claims-based policy unauthenticated (pitfall 21).
- [ ] No new `Task.Run` in a request path; every async call awaited.
- [ ] Layer references still point downward only; no new upward `ProjectReference`.
- [ ] Docs updated **in this change**: `CLAUDE.md` decisions, `ARCHITECTURE.md` flows, `API-CONTRACT.md` contract, `TROUBLESHOOTING.md` if something new was debugged.
- [ ] Every non-obvious decision has a recorded *why* and its consequence if forgotten.
- [ ] Nothing outside the scope of this change was modified.
