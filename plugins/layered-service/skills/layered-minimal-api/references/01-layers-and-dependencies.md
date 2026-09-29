# 01 — Layers and dependencies

## The rule

Four projects, one dependency direction. Every project references only **downward**. `Domain`
references nothing.

Why: a dependency that points upward is a dependency that can never be tested, replaced or reasoned
about in isolation. Most of the "we need a workaround" stories in production come from one layer
knowing about the layer above it.

## The graph

```
Contoso.Orders.Api                      Sdk="Microsoft.NET.Sdk.Web"
  -> Contoso.Orders.Application.UseCases
  -> Contoso.Orders.Infrastructure.Persistence
       -> Contoso.Orders.Domain

Contoso.Orders.Application.UseCases
  -> Contoso.Orders.Infrastructure.Persistence
       -> Contoso.Orders.Domain
```

`Api` references `Infrastructure.Persistence` directly as well as `Application.UseCases` — it is the
composition root and it legitimately needs the `DbContext` factory and the audit service. Nothing else
may.

## What goes in each layer

### Domain — no `ProjectReference` at all

Models, enums, and the telemetry instruments (`Meter`, `ActivitySource`, counters).
Read `references/03-domain.md`.

### Infrastructure.Persistence

- `<Product>DbContext` — one `DbContext`, `DbSet<T>` grouped by module.
- `Configurations/<Module>/<Entity>Configuration.cs` — one `IEntityTypeConfiguration<T>` per entity.
- `Contexts/<Module>/I<Module>Context.cs` + `<Module>Context.cs` — hand-written queries per module.
- `Batching/` — reusable batch accumulators.
- `DependencyInjection.cs` — `AddPersistence(services, configuration)`.

Read `references/04-persistence-efcore.md`.

### Application.UseCases

```
Commons/
  DTOs/            request/response DTOs
  Validations/     FluentValidation AbstractValidator<TDto>, one per request DTO
  Mappings/        Mapster TypeAdapterConfig, one per module + a global initializer
  Utils/           pure stateless helpers (pagination, time, hashing, normalization)
Features/          handlers, no subfolder per integration
IFeatures/         handler interfaces
IServices/         ports to infrastructure implemented in Api (IQueueService, ICarrierService, ...)
DependencyInjection.cs
```

Handlers live **flat** in `Features/`, one class per integration, no per-integration subfolder. Why:
with a handful of handlers a flat directory is faster to scan than a tree of one-item folders, and it
stops the tree from being re-organized every time a second handler appears.

Read `references/05-application-usecases.md`.

### Api — `Sdk="Microsoft.NET.Sdk.Web"`

```
Endpoints/<Module>Endpoints.cs   static class + Map<Module>Endpoints() extension
Filters/                         ValidationFilter<T>, IdValidationFilter
Middlewares/                     (only what a global IExceptionHandler cannot do)
Security/                        options + custom IAuthorizationRequirement/Handler
RateLimiting/                    one static class per policy + options
Observability/                   AddObservability extension
Services/<Provider>/             the only folder that references a third-party SDK
Commons/                         result-to-HTTP helpers
Program.cs
```

## Folder tree

```
<Company>.<Product>/
  <Company>.<Product>.slnx
  CLAUDE.md
  Directory.Build.props
  global.json
  .editorconfig
  src/
    <Company>.<Product>.Domain/
      Entities/  Models/  Enums/  Telemetry/
    <Company>.<Product>.Infrastructure.Persistence/
      <Product>DbContext.cs  SqlOptions.cs
      Configurations/<Module>/  Contexts/<Module>/  Batching/
      DependencyInjection.cs
    <Company>.<Product>.Application.UseCases/
      Commons/{DTOs,Validations,Mappings,Utils}/
      Features/  IFeatures/  IServices/
      DependencyInjection.cs
    <Company>.<Product>.Api/
      Endpoints/  Filters/  Middlewares/  Security/  RateLimiting/
      Observability/  Services/<Provider>/  Commons/
      Program.cs  appsettings.json
  tests/
    <Company>.<Product>.Domain.Tests/
    <Company>.<Product>.Infrastructure.Persistence.Tests/
    <Company>.<Product>.Application.UseCases.Tests/
    <Company>.<Product>.Api.Tests/
  docs/
    ARCHITECTURE.md  API-CONTRACT.md  DEPLOYMENT.md  TROUBLESHOOTING.md
```

Tests **mirror** the layers: one test project per layer, named after it. The mirror is what makes it
obvious which layer has no test project yet.

## Solution file

`ProjectReference` is only half the wiring: each project also needs its own `PackageReference` set. A
class library starts with no framework references at all, and the Web SDK pre-imports ASP.NET
namespaces but not their packages, so the missing ones show up as unresolvable *types*
(`BackgroundService`, `IQueueService`, `AddMapster`, `AddOpenApi`) rather than as a missing package.
The verified per-project set is in `references/15-checklists.md` §(a2).

Use a `.slnx` with virtual folders, not a classic `.sln`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Contoso.Orders.Domain/Contoso.Orders.Domain.csproj" />
    <Project Path="src/Contoso.Orders.Infrastructure.Persistence/Contoso.Orders.Infrastructure.Persistence.csproj" />
    <Project Path="src/Contoso.Orders.Application.UseCases/Contoso.Orders.Application.UseCases.csproj" />
    <Project Path="src/Contoso.Orders.Api/Contoso.Orders.Api.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Contoso.Orders.Infrastructure.Persistence.Tests/Contoso.Orders.Infrastructure.Persistence.Tests.csproj" />
    <Project Path="tests/Contoso.Orders.Application.UseCases.Tests/Contoso.Orders.Application.UseCases.Tests.csproj" />
    <Project Path="tests/Contoso.Orders.Api.Tests/Contoso.Orders.Api.Tests.csproj" />
  </Folder>
</Solution>
```

Why: it is XML, it diffs cleanly, and it does not regenerate a new GUID on every `sln add`.

## Anti-patterns

- `Domain` with a `ProjectReference` to anything. Domain models that need a package belong in the layer
  that owns the package.
- `Application.UseCases` referencing `Api` (it usually means a port interface was put in the wrong
  project).
- A fifth project created "just for this one integration". The rule is four layers, not four projects
  plus exceptions.
- Folder per handler once there are more than a handful — the tree becomes the navigation problem.
- Tests in one giant project. Then nobody knows which layer is untested.
- A classic `.sln` regenerated by tooling and never formatted.
