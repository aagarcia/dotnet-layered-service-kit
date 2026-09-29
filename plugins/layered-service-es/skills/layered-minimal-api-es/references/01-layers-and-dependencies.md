# 01 — Capas y dependencias

## La regla

Cuatro proyectos, una sola dirección de dependencias. Cada proyecto referencia únicamente **hacia
abajo**. `Domain` no referencia nada.

Por qué: una dependencia que apunta hacia arriba es una dependencia que nunca podrá testearse,
reemplazarse ni razonarse por separado. La mayoría de las historias de «necesitamos un workaround» en
producción vienen de que una capa conoce a la de arriba.

## El grafo

```
Contoso.Orders.Api                      Sdk="Microsoft.NET.Sdk.Web"
  -> Contoso.Orders.Application.UseCases
  -> Contoso.Orders.Infrastructure.Persistence
       -> Contoso.Orders.Domain

Contoso.Orders.Application.UseCases
  -> Contoso.Orders.Infrastructure.Persistence
       -> Contoso.Orders.Domain
```

`Api` referencia `Infrastructure.Persistence` directamente además de `Application.UseCases` — es la
raíz de composición y legítimamente necesita la factory del `DbContext` y el servicio de auditoría.
Nadie más puede hacerlo.

## Qué va en cada capa

### Domain — sin `ProjectReference` alguno

Modelos, enums e instrumentos de telemetría (`Meter`, `ActivitySource`, contadores).
Lee `references/03-domain.md`.

### Infrastructure.Persistence

- `<Product>DbContext` — un `DbContext`, con los `DbSet<T>` agrupados por módulo.
- `Configurations/<Module>/<Entity>Configuration.cs` — un `IEntityTypeConfiguration<T>` por entidad.
- `Contexts/<Module>/I<Module>Context.cs` + `<Module>Context.cs` — queries escritas a mano por módulo.
- `Batching/` — acumuladores por lotes reutilizables.
- `DependencyInjection.cs` — `AddPersistence(services, configuration)`.

Lee `references/04-persistence-efcore.md`.

### Application.UseCases

```
Commons/
  DTOs/            DTOs de request/response
  Validations/     AbstractValidator<TDto> de FluentValidation, uno por DTO de request
  Mappings/        TypeAdapterConfig de Mapster, uno por módulo + un inicializador global
  Utils/           helpers puros sin estado (paginación, tiempo, hash, normalización)
Features/          handlers, sin subcarpeta por integración
IFeatures/         interfaces de los handlers
IServices/         puertos hacia infraestructura implementados en Api (IQueueService, ICarrierService, ...)
DependencyInjection.cs
```

Los handlers viven **planos** en `Features/`, una clase por integración, sin subcarpeta por
integración. Por qué: con unos pocos handlers un directorio plano se recorre más rápido que un árbol
de carpetas de un elemento, y evita tener que reorganizar el árbol cada vez que aparece un segundo
handler.

Lee `references/05-application-usecases.md`.

### Api — `Sdk="Microsoft.NET.Sdk.Web"`

```
Endpoints/<Module>Endpoints.cs   static class + extensión Map<Module>Endpoints()
Filters/                         ValidationFilter<T>, IdValidationFilter
Middlewares/                     (solo lo que un IExceptionHandler global no pueda hacer)
Security/                        options + IAuthorizationRequirement/Handler propios
RateLimiting/                    una static class por policy + options
Observability/                   extensión AddObservability
Services/<Provider>/             la única carpeta que referencia un SDK de terceros
Commons/                         helpers de resultado a HTTP
Program.cs
```

## Árbol de carpetas

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
    ARQUITECTURA.md  API-CONTRATO.md  DEPLOYMENT.md  TROUBLESHOOTING.md
```

Los tests **reflejan** las capas: un proyecto de test por capa, nombrado como ella. El reflejo es lo
que hace obvio qué capa todavía no tiene proyecto de test.

## Archivo de solución

`ProjectReference` es solo la mitad del cableado: cada proyecto necesita además su propio conjunto de
`PackageReference`. Una librería de clases arranca sin ninguna referencia de framework, y el Web SDK
pre-importa los namespaces de ASP.NET pero no sus paquetes, así que los que faltan aparecen como
*tipos* irresolubles (`BackgroundService`, `IQueueService`, `AddMapster`, `AddOpenApi`) y no como un
paquete faltante. El conjunto verificado por proyecto está en
`references/15-checklists.md` §(a2).

Usa un `.slnx` con carpetas virtuales, no un `.sln` clásico:

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

Por qué: es XML, diffea limpio y no regenera GUIDs cada vez que agregas un proyecto.

## Anti-patrones

- `Domain` con un `ProjectReference` a algo. Los modelos de dominio que necesiten un paquete van en la
  capa que es dueña del paquete.
- `Application.UseCases` referenciando `Api` (suele significar que un puerto quedó en el proyecto
  equivocado).
- Un quinto proyecto creado «solo para esta integración». La regla son cuatro capas, no cuatro
  proyectos más excepciones.
- Una carpeta por handler cuando ya hay varios. El árbol pasa a ser el problema de navegación.
- Todos los tests en un proyecto gigante. Así nadie ve qué capa está sin testear.
- Un `.sln` clásico regenerado por herramientas y nunca formateado.
