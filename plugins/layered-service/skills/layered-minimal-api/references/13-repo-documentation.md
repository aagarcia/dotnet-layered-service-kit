# 13 — Repository documentation

## The rule

Four documents, each with a distinct job, and one rule about how decisions are recorded: **every
non-obvious decision gets its *why*, in the same place the decision lives.**

Why documentation rots when it only records *what*: the reader can already read the code, so the only
value a document adds is the reasoning that is no longer in the code. A file full of restated
signatures is worse than no file, because it looks authoritative and it is wrong within a month.

## `CLAUDE.md` — the working manual

The most-read file in the repo. It orients an agent or a new developer in under two minutes.

Must contain, in this order:

1. **What this is** — one paragraph, and which layer each project is.
2. **Commands** — build, test, coverage, run locally, publish. Copy-pasteable, not descriptions.
3. **The core rule** — the thin-I/O-shell rule, with the concrete pairs in *this* repo.
4. **Dependency graph and DI** — which project references which, and the three registration points.
5. **Non-obvious decisions** — each one with its reason (see below).
6. **Conventions** — the choices a reader would otherwise have to infer.

`docs/CLAUDE.md.template` in `templates/` is a fill-in-the-blanks starting point.

## `docs/ARCHITECTURE.md` — the shape and the flows

- An ASCII component diagram: the callers, the API, the layers, every external system.
- The dependency graph.
- **Happy path**, step by step, naming the classes involved at each step.
- **Error path**, as a table: what the caller sent, what happened, which code answered, and why that
  code. A row per rejection, including the ones the framework answers before the handler runs.
- A decisions table: `Decision | Why`.

## `docs/API-CONTRACT.md` — the wire contract

- Every route: method, path, auth required, rate-limit policy, request schema, response schema, every
  status code it can return.
- Error model: the ProblemDetails shape, the `errors` dictionary, and the stable message per code.
- The queue contract: name, message schema, version, what a consumer must tolerate.
- Versioning rule: what is additive, what needs a new version.

## `docs/DEPLOYMENT.md` — how it gets live

- Target platform, publish command, artifact, zip/container steps.
- Required configuration keys and where each secret comes from — named, never valued.
- Health and readiness endpoints.
- Post-deploy verification steps.

## `docs/TROUBLESHOOTING.md` — the symptom index

One entry per symptom that has actually happened, in the form
`Symptom | Likely cause | How to confirm | Fix`. Write it when you debug something, while you still
remember what it looked like. This is the file that gets read under pressure.

## Recording decisions

Format, every time:

```markdown
- **Decision in one line.** Because the reason that forced it. Consequence if we forget.
```

The third clause is what makes a decision stick. Examples of the shape:

```markdown
- **Normalization happens in the API, before enqueuing, not in the consumer.** Because every consumer
  would otherwise re-implement the same mapping, and the moment the upstream format changes, all of
  them break at once. If you move it, the queue contract changes and the consumers must be redeployed
  together.
```

```markdown
- **The business time zone is a fixed UTC-05:00 offset, never the host's zone.** Because parsing with
  the host's zone makes the same input produce a different instant in a laptop and in the production
  region. If you parse with the local zone you will ship an off-by-one-hour report that only shows up
  in production.
```

```markdown
- **Rate limiting is per instance, not distributed.** Because the built-in limiter is in-process. With N
  instances the effective limit is N times the configured value; if that matters, the limits must move
  to a shared store.
```

Test for each entry: *would a competent developer, reading only the code, make a different choice?* If
not, it does not belong in the file.

## What not to document

- Restatements of signatures. The code is the reference.
- A changelog. Use version control.
- Aspirational architecture. If it is not built, it is not in `ARCHITECTURE.md`.
- Secrets, connection strings, client ids, tenant ids, internal hostnames.
- The team's names or internal URLs.

## Keeping docs alive

- Update the docs **in the same change** that alters the design. A follow-up "update the docs" task is
  a task that never happens.
- Review `docs/ARCHITECTURE.md`'s decisions table during any architecture review; stale decisions are
  usually decisions that were reversed.
- The reviewer agent (`agents/service-architecture-reviewer.md`) treats a missing non-obvious-decision
  entry as a finding. That is intentional: the reasoning is the only part documentation uniquely holds.
