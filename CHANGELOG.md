# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Issue templates, pull request template and a collaboration status note in the READMEs.
- `references/16-choosing-data-access.md` (EN/ES): for every endpoint and verb (GET, POST, PUT, PATCH,
  DELETE), classify the operation, gather the facts and tell the developer the plan and a reasoned
  decision between EF Core (default), `ExecuteUpdate`/`ExecuteDelete`, bulk, enqueue-and-202, raw SQL /
  `SqlQuery<T>` / Dapper, and CQRS. Wired into the skill workflow (step 1), golden rule 26 and the
  reference index.
- `references/17-caching-and-redis.md` (EN/ES): Redis and caching as an option the developer decides on.
  Claude gives a verdict (viable / viable with conditions / not recommended), where it fits, key and TTL
  rules, post-commit invalidation and fail-open behavior. Golden rule 27 and the reference index.
- Optional templates `CachingExtensions.cs` (HybridCache, Redis second level only when a connection
  string exists) and `CachedOrderStatusReader.cs` (decorator behind a port, pure key builder, fail-open,
  post-commit invalidation); template count is now 14. Compiled against net10.0; not yet run against a
  live Redis.
- Skill workflow closing step: unused `using` cleanup (IDE0005) with the zero-warnings build.
- READMEs (EN/ES): table of the 17 reference files, reviewer agent usage, and a note on unpinned
  package versions (`SonarAnalyzer.CSharp` placeholder).

### Fixed

- READMEs: template count is 12, not 11.
- `SonarAnalyzer.CSharp` placeholder is now the exact range `[0.0.0-PLACEHOLDER]`, so restore really fails
  with NU1102 as documented (a bare version is only a minimum and resolved with a NU1603 warning).

### Planned

- Worker standard (queue consumers and `BackgroundService` processing), as a separate plugin.

## [0.1.0] - 2026-09-29

First public release.

### Added

- `layered-service` plugin (English):
  - `layered-minimal-api` skill — architecture, golden rules and workflow for layered .NET 10
    Minimal API services.
  - 15 reference files: layers and dependencies, the thin I/O shell core rule, domain, EF Core
    persistence, use cases, API presentation, security and rate limiting, async ingestion (`202`),
    third-party SDKs, observability, configuration, testing, repo documentation, production pitfalls
    and checklists.
  - 12 copy-ready templates: `Program.cs`, both `DependencyInjection.cs`, `ValidationFilter.cs`,
    `ModuleEndpoints.cs`, `GlobalExceptionHandler.cs`, `ObservabilityExtensions.cs`,
    `Directory.Build.props`, `global.json`, `.editorconfig`, `.gitignore` fragment and
    `CLAUDE.md.template`.
  - `service-architecture-reviewer` read-only audit agent.
- `layered-service-es` plugin: the same content in Spanish, with unique skill and agent names so both
  plugins can be installed side by side.
- `dotnet-layered-service-kit` marketplace exposing both plugins.

[Unreleased]: https://github.com/aagarcia/dotnet-layered-service-kit/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/aagarcia/dotnet-layered-service-kit/releases/tag/v0.1.0
