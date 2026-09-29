# Contributing

Thanks for helping improve the kit. Issues and pull requests are welcome in English or Spanish.

## Reporting a problem

Open an issue with:

- which rule, reference file or template is affected;
- what happened (or what the agent generated) and what you expected;
- for production pitfalls: the symptom, the cause and the fix you applied.

## Changing the standard

1. **Say why.** Every rule in the kit states the reason it exists. A new or changed rule without a
   reason will not be merged.
2. **Keep both languages in sync.** A change to `plugins/layered-service/` must land in
   `plugins/layered-service-es/` in the same pull request, and vice versa. File names under
   `references/` and `templates/` are identical in both plugins, so the pair is easy to diff.
3. **Templates must build.** Code in `templates/` and in reference snippets targets .NET 10 / C# 14 and
   must compile with zero warnings once the `<Company>`, `<Product>` and `<Module>` placeholders are
   replaced.
4. **Stay in scope.** The kit covers the HTTP service only. Worker and queue-consumer processing
   belongs to the upcoming worker standard.

## Versioning and releases

The project follows [Semantic Versioning](https://semver.org/). Add your change under
`[Unreleased]` in [CHANGELOG.md](CHANGELOG.md). When a release is cut, the version is bumped in
`.claude-plugin/marketplace.json` and in both `plugins/*/.claude-plugin/plugin.json`, the changelog
section is dated, and the commit is tagged `vX.Y.Z`.
