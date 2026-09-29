## What and why

<!-- What changes, and the reason for it. Every rule in the kit states why it exists. -->

## Checklist

- [ ] The change is applied to both `plugins/layered-service/` (EN) and `plugins/layered-service-es/` (ES)
- [ ] Templates and snippets compile on .NET 10 with zero warnings (placeholders replaced)
- [ ] Stays within scope: HTTP service only, no worker processing
- [ ] Entry added under `[Unreleased]` in `CHANGELOG.md`
- [ ] `claude plugin validate .` passes (if manifests changed)
