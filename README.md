# dotnet-layered-service-kit

[![Release](https://img.shields.io/github/v/release/aagarcia/dotnet-layered-service-kit)](https://github.com/aagarcia/dotnet-layered-service-kit/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Claude Code plugin](https://img.shields.io/badge/Claude%20Code-plugin-D97757)](https://docs.claude.com/en/docs/claude-code/plugins)

A reusable standard for **building and reviewing layered .NET 10 Minimal API services**.

It ships as a **Claude Code plugin**: one skill that teaches an agent (or a person) how to scaffold,
implement, test and document a layered HTTP service, plus one read-only agent that audits an existing
service against the same standard. Every rule states *why* it exists, and the last reference file is a
symptom → cause → fix catalog of problems that only show up in production.

[Español →](README.es.md)

> **Status: early, open for collaboration.** This is a community project in active development. It is
> **not** listed in the official Claude plugin directory; you install it straight from this repository.
> Feedback on the rules, the templates and the pitfalls catalog is very welcome — open an issue or
> start a discussion.

## What it is

A **service** here means one HTTP API: a solution with four projects, one dependency direction, and a
single rule that keeps logic testable:

```
Api  (Sdk.Web)              Minimal API, auth, rate limiting, ProblemDetails, third-party SDKs
  -> Application.UseCases   DTOs, FluentValidation, Mapster, handlers
      -> Infrastructure.Persistence   EF Core, queue clients, batch writers
          -> Domain      plain models, enums, telemetry instruments
```

**The core rule — thin I/O shell + testable logic.** Every class that opens real I/O (SQL, queue, HTTP,
a vendor SDK) stays a thin shell. The logic it needs — mapping, merging, batching, pagination, error
translation — lives in separate pure classes that are unit-testable with no network, no database and no
Azure account. See `references/02-core-rule-thin-io-shell.md`.

Scope is **the HTTP service only**. There is no worker, no queue consumer and no `BackgroundService`
that does the business processing here. (The in-process helpers the service itself needs — a bounded
channel plus a batch writer for non-blocking audit logging — are in scope, because the service owns
them. Roadmap: worker standard, coming later.)

## What is in the box

| Path | What it gives you |
|---|---|
| `plugins/layered-service/skills/layered-minimal-api/SKILL.md` | The operating instructions: architecture, golden rules, workflow, index |
| `plugins/layered-service/skills/layered-minimal-api/references/` | 15 topic files: layers, the core rule, domain, persistence, use cases, API, security, async ingestion, third-party SDKs, observability, configuration, testing, docs, **production pitfalls**, checklists |
| `plugins/layered-service/skills/layered-minimal-api/templates/` | 11 files ready to copy: `Program.cs`, the two `DependencyInjection.cs`, `ValidationFilter.cs`, `ModuleEndpoints.cs`, `GlobalExceptionHandler.cs`, `ObservabilityExtensions.cs`, `Directory.Build.props`, `global.json`, `.editorconfig`, `.gitignore` fragment, `CLAUDE.md.template` |
| `plugins/layered-service/agents/service-architecture-reviewer.md` | Read-only audit agent; outputs findings by severity with `file:line` evidence |
| `plugins/layered-service-es/…` | The same content in Spanish, installable side by side |

The English and Spanish plugins have **different skill and agent names** (`layered-minimal-api` vs
`layered-minimal-api-es`, `service-architecture-reviewer` vs `service-architecture-reviewer-es`) so both
can be installed at once. The `references/` and `templates/` **file names are identical** in both, so
you can diff them or symlink them if you prefer.

## Install

### 1. As a plugin (recommended)

```bash
# straight from GitHub
/plugin marketplace add aagarcia/dotnet-layered-service-kit
/plugin install layered-service@dotnet-layered-service-kit
```

Spanish version:

```bash
/plugin install layered-service-es@dotnet-layered-service-kit
```

Then just ask for the work — for example *"create a .NET 10 service that receives orders, validates
them and returns 202"* — and the skill triggers on its own. To audit an existing service, invoke the
`service-architecture-reviewer` agent.

### 2. Manual copy into a project

No plugin machinery needed; copy the files into the target repo:

```bash
# the skill
cp -r plugins/layered-service/skills/layered-minimal-api/  <project>/.claude/skills/
# the reviewer agent
cp plugins/layered-service/agents/service-architecture-reviewer.md <project>/.claude/agents/
```

For the Spanish version copy from `plugins/layered-service-es/` instead, and rename nothing — the
skill and agent names are already unique.

The templates are copied by hand as you need them, not in bulk:

```bash
cp plugins/layered-service/skills/layered-minimal-api/templates/Directory.Build.props <project>/
cp plugins/layered-service/skills/layered-minimal-api/templates/Program.cs \
   <project>/src/<Company>.<Product>.Api/Program.cs
```

Every template carries `<Company>`, `<Product>` and `<Module>` placeholders. Replace them before you
build.

## The example domain

The kit teaches with exactly one domain — **Orders** — so examples stay short and comparable:

- an ingestion endpoint that receives orders from an upstream partner, validates them, normalizes
  them, enqueues them and answers `202 Accepted`;
- a `GET` query that reads shipment status from a fictional third-party **Carrier API** SDK.

Namespaces are `<Company>.<Product>.<Layer>`; concrete snippets use `Contoso.Orders.*`.

## Conventions this standard assumes

- .NET 10, C# 14, `Nullable` and `ImplicitUsings` enabled repository-wide.
- **Primary constructors** in production code, not constructor-overload base classes.
- Minimal APIs — no controllers.
- `ProblemDetails` for every non-2xx response, including `401`, `403`, `429` and unhandled errors.
- Framework-native mechanisms: `AddJwtBearer`, `AddRateLimiter`, OpenTelemetry. Never re-implement one.
- **No CORS** in a server-to-server API.
- Unit tests on pure logic; integration tests on endpoints with `WebApplicationFactory`; EF tests on
  **SQLite in-memory**, plus a manual pass against the real database engine for anything the bulk or
  retry paths touch.
- `dotnet build` with **zero warnings** is part of "done".

## Conventions you must choose deliberately

Two decisions are not the standard's to make. Pick one per repo and write it down in `CLAUDE.md`:

1. **Property language.** Types in English, always. Properties may follow the upstream system or the
   database naming — pick one convention and state it, because a half-translated model is worse than
   either.
2. **Business time zone.** Always a fixed offset from the business (e.g. `UTC-05:00`), never the
   operating system's zone, or dev and prod disagree.

To pin a specific release, add the marketplace at a tag:
`/plugin marketplace add https://github.com/aagarcia/dotnet-layered-service-kit.git#v0.1.0`.
From a local clone, `/plugin marketplace add /path/to/dotnet-layered-service-kit` works too.

## Versioning

The kit follows [Semantic Versioning](https://semver.org/). While it is `0.x`, the standard may still
change in ways that break a service built against an earlier version; every change is listed in
[CHANGELOG.md](CHANGELOG.md). `1.0.0` will mark the standard as stable.

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). A rule change must say
*why*, and must land in both the English and the Spanish plugin.

## License

MIT — see [LICENSE](LICENSE).
