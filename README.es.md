# dotnet-layered-service-kit

[![Release](https://img.shields.io/github/v/release/aagarcia/dotnet-layered-service-kit)](https://github.com/aagarcia/dotnet-layered-service-kit/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Claude Code plugin](https://img.shields.io/badge/Claude%20Code-plugin-D97757)](https://docs.claude.com/en/docs/claude-code/plugins)

Estándar reutilizable para **crear y revisar services .NET 10 Minimal API en capas**.

Se distribuye como **plugin de Claude Code**: una skill que enseña a un agente (o a una persona) cómo
crear, implementar, testear y documentar un service HTTP en capas, más un agente de solo lectura que
audita un service existente contra el mismo estándar. Cada regla dice *por qué* existe, y el último
archivo de references es un catálogo síntoma → causa → solución de problemas que solo aparecen en
producción.

[English →](README.md)

> **Estado: temprano, abierto a colaboración.** Es un proyecto comunitario en desarrollo activo. **No**
> está publicado en el directorio oficial de plugins de Claude; se instala directamente desde este
> repositorio. Se agradece cualquier comentario sobre las reglas, las plantillas y el catálogo de
> pitfalls — abre un issue o una discusión.

## Qué es

Un **service** aquí significa una API HTTP: una solución con cuatro proyectos, una sola dirección de
dependencias y una regla que mantiene la lógica testeable:

```
Api  (Sdk.Web)              Minimal API, auth, rate limiting, ProblemDetails, SDKs de terceros
  -> Application.UseCases   DTOs, FluentValidation, Mapster, handlers
      -> Infrastructure.Persistence   EF Core, queue clients, batch writers
          -> Domain      modelos planos, enums, instrumentos de telemetría
```

**La regla central: shell de I/O delgado + lógica testeable.** Toda clase que abre I/O real (SQL, cola,
HTTP, un SDK de proveedor) queda como un shell delgado. La lógica que necesita — mapeo, merge, batching,
paginación, traducción de errores — vive en clases puras separadas, testeables por unidad sin red, sin
base de datos y sin cuenta en la nube. Ver `references/02-core-rule-thin-io-shell.md`.

El alcance es **solo el service HTTP**. Aquí no hay worker, ni consumidor de cola, ni
`BackgroundService` que haga el procesamiento de negocio. (Los auxiliares en proceso que el propio
service necesita — un canal acotado más un escritor por lotes para la auditoría no bloqueante — sí
entran, porque el service es su dueño. Roadmap: estándar de worker, más adelante.)

## Qué trae

| Ruta | Qué te da |
|---|---|
| `plugins/layered-service-es/skills/layered-minimal-api-es/SKILL.md` | Las instrucciones de trabajo: arquitectura, reglas de oro, flujo, índice |
| `plugins/layered-service-es/skills/layered-minimal-api-es/references/` | 15 archivos por tema: capas, regla central, dominio, persistencia, casos de uso, API, seguridad, ingesta asíncrona, SDKs de terceros, observabilidad, configuración, testing, docs, **pitfalls de producción**, checklists |
| `plugins/layered-service-es/skills/layered-minimal-api-es/templates/` | 11 archivos listos para copiar: `Program.cs`, los dos `DependencyInjection.cs`, `ValidationFilter.cs`, `ModuleEndpoints.cs`, `GlobalExceptionHandler.cs`, `ObservabilityExtensions.cs`, `Directory.Build.props`, `global.json`, `.editorconfig`, fragmento de `.gitignore`, `CLAUDE.md.template` |
| `plugins/layered-service-es/agents/service-architecture-reviewer-es.md` | Agente de auditoría de solo lectura; entrega hallazgos por severidad con evidencia `archivo:línea` |
| `plugins/layered-service/…` | El mismo contenido en inglés, instalable en paralelo |

Los plugins inglés y español tienen **nombres de skill y agente distintos**
(`layered-minimal-api` vs `layered-minimal-api-es`, `service-architecture-reviewer` vs
`service-architecture-reviewer-es`) para que ambos se puedan instalar a la vez. Los **nombres de
archivo** de `references/` y `templates/` son idénticos en las dos versiones, así que puedes
diffearlos o enlazarlos si prefieres.

## Instalación

### 1. Como plugin (recomendado)

```bash
# directamente desde GitHub
/plugin marketplace add aagarcia/dotnet-layered-service-kit
/plugin install layered-service-es@dotnet-layered-service-kit
```

Versión en inglés:

```bash
/plugin install layered-service@dotnet-layered-service-kit
```

Después solo pide el trabajo — por ejemplo *"crea un service .NET 10 que reciba pedidos, los valide y
devuelva 202"* — y la skill se activa sola. Para auditar un service existente, invoca el agente
`service-architecture-reviewer-es`.

### 2. Copia manual a un proyecto

No hace falta la maquinaria de plugins; copia los archivos al repo destino:

```bash
# la skill
cp -r plugins/layered-service-es/skills/layered-minimal-api-es/ <proyecto>/.claude/skills/
# el agente revisor
cp plugins/layered-service-es/agents/service-architecture-reviewer-es.md <proyecto>/.claude/agents/
```

Para la versión en inglés copia desde `plugins/layered-service/` y no renombres nada — los nombres de
skill y agente ya son únicos.

Las plantillas se copian a mano a medida que hacen falta, no todas de una:

```bash
cp plugins/layered-service-es/skills/layered-minimal-api-es/templates/Directory.Build.props <proyecto>/
cp plugins/layered-service-es/skills/layered-minimal-api-es/templates/Program.cs \
   <proyecto>/src/<Company>.<Product>.Api/Program.cs
```

Todas las plantillas traen placeholders `<Company>`, `<Product>` y `<Module>`. Reemplázalos antes de
compilar.

## El dominio de ejemplo

El kit enseña con un único dominio — **Orders** — para que los ejemplos se mantengan cortos y
comparables:

- un endpoint de ingesta que recibe pedidos de un partner upstream, los valida, los normaliza, los
  encola y responde `202 Accepted`;
- una consulta `GET` que lee el estado de un envío desde el SDK de una **Carrier API** ficticia de
  terceros.

Los namespaces son `<Company>.<Product>.<Layer>`; los snippets concretos usan `Contoso.Orders.*`.

## Convenciones que este estándar asume

- .NET 10, C# 14, `Nullable` e `ImplicitUsings` habilitados en todo el repo.
- **Primary constructors** en el código de producción, no clases base con sobrecargas de constructor.
- Minimal APIs — sin controllers.
- `ProblemDetails` en toda respuesta no-2xx, incluidos `401`, `403`, `429` y errores no controlados.
- Mecanismos nativos del framework: `AddJwtBearer`, `AddRateLimiter`, OpenTelemetry. Nunca reimplementar
  uno.
- **Sin CORS** en una API servidor-a-servidor.
- Tests unitarios de la lógica pura; tests de endpoints con `WebApplicationFactory`; tests de EF sobre
  **SQLite in-memory**, más una pasada manual contra el motor real para todo lo que toque los caminos
  de bulk o de retry.
- `dotnet build` con **cero warnings** es parte de "terminado".

## Convenciones que debes elegir a propósito

Dos decisiones no le corresponden al estándar. Elige una por repo y escríbela en `CLAUDE.md`:

1. **Idioma de las propiedades.** Los tipos, siempre en inglés. Las propiedades pueden seguir el
   idioma del sistema origen o el de la base de datos — elige una convención y declárala, porque un
   modelo traducido a medias es peor que cualquiera de las dos.
2. **Zona horaria del negocio.** Siempre un offset fijo del negocio (p. ej. `UTC-05:00`), nunca la
   zona del sistema operativo, o dev y prod discreparán.

Para fijar una versión concreta, añade el marketplace en un tag:
`/plugin marketplace add https://github.com/aagarcia/dotnet-layered-service-kit.git#v0.1.0`.
Desde un clon local también funciona `/plugin marketplace add /ruta/a/dotnet-layered-service-kit`.

## Versionado

El kit sigue [Semantic Versioning](https://semver.org/lang/es/). Mientras sea `0.x`, el estándar puede
cambiar de formas que rompan un service construido con una versión anterior; cada cambio queda en
[CHANGELOG.md](CHANGELOG.md). La `1.0.0` marcará el estándar como estable.

## Contribuir

Issues y pull requests son bienvenidos — ver [CONTRIBUTING.md](CONTRIBUTING.md). Un cambio de regla
debe explicar *por qué* y aplicarse tanto al plugin en inglés como al de español.

## Licencia

MIT — ver [LICENSE](LICENSE).
