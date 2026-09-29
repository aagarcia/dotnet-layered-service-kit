# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Issue templates, pull request template and a collaboration status note in the READMEs.

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
  - 11 copy-ready templates: `Program.cs`, both `DependencyInjection.cs`, `ValidationFilter.cs`,
    `ModuleEndpoints.cs`, `GlobalExceptionHandler.cs`, `ObservabilityExtensions.cs`,
    `Directory.Build.props`, `global.json`, `.editorconfig`, `.gitignore` fragment and
    `CLAUDE.md.template`.
  - `service-architecture-reviewer` read-only audit agent.
- `layered-service-es` plugin: the same content in Spanish, with unique skill and agent names so both
  plugins can be installed side by side.
- `dotnet-layered-service-kit` marketplace exposing both plugins.

[Unreleased]: https://github.com/aagarcia/dotnet-layered-service-kit/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/aagarcia/dotnet-layered-service-kit/releases/tag/v0.1.0
