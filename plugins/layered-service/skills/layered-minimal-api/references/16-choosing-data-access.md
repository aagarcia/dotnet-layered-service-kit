# 16 — Choosing the approach for any endpoint (EF Core, raw SQL / Dapper, queue, CQRS)

Use this file **for every endpoint the developer asks for, whatever the verb**: `GET`, `POST`, `PUT`,
`PATCH`, `DELETE` or anything else. Do not pick silently: before writing code, tell the developer what
you are about to do, which approach you chose, why, and what you rejected. Then implement it.

## Step 1 — Classify the operation

| Operation | What has to be decided |
|---|---|
| `GET` (one / list / report) | How to read: EF Core projection, raw SQL, or a separate read model |
| `POST` create | Single insert, batch insert, or no database write at all (enqueue and answer `202`) |
| `POST` command / action | A state change that is not a plain insert: transaction scope, idempotency, side effects |
| `PUT` | Full replace: load-and-set or set-based update; concurrency |
| `PATCH` | Partial update: which fields, load-and-modify or set-based update; concurrency |
| `DELETE` | Hard delete or soft delete; set-based or by entity; idempotency |
| `HEAD`, `OPTIONS`, other | Usually nothing to decide; say so and go on |

## Step 2 — Get the facts (ask at most two questions; otherwise state your assumptions)

| Fact | Why it decides |
|---|---|
| Shape: one row, a page, a joined projection, an aggregate, an aggregate root with children | Simple shapes are EF Core's strength; heavy joins and aggregates are candidates for raw SQL, but only a measurement proves EF translates them badly |
| Volume and frequency: rows per call, calls per second, latency target | Only a measured or clearly large path justifies leaving the default |
| Must the caller see the result immediately, or can the work happen later? | If it can wait, the endpoint may only enqueue (reference 08) |
| Transaction and consistency: one table or several, what must commit together | A relational `SaveChanges` is atomic by default. Use an explicit transaction when several calls or commands must commit together, wrapped in the configured execution strategy (reference 04) |
| Concurrency: can two clients update the same row? Retries from the client? | Decides a concurrency token and an idempotency key |
| Same tables and model as the other side (reads vs writes)? | Divergence is this kit's threshold for separate read projections or stores |
| Database engine and features needed (window functions, CTEs, JSON, full-text) | EF Core may not translate them well |
| Is the read repeated, slow, or spread over several instances? Did the developer mention cache or Redis? | Open reference 17 and give a cache verdict; the default is no cache |

## Step 3 — Apply the decision

### Reads (`GET`)

**Default: EF Core, read-only.** For lookups by key, filtered or paged lists, small joins.

- `AsNoTracking()` and project to the DTO with `Select(...)`; never return entities.
- Always page lists (`Skip`/`Take` with a stable `OrderBy`, keyset for large tables).
- Why: one tool and one configuration; the query is hand-written in the module `Context` and
  reviewable (reference 04).

**Raw SQL when the read is SQL-shaped:** multi-table joins with aggregates, window functions, CTEs,
reports, or a path where you **measured** EF Core's translation as the bottleneck.

- Try `Database.SqlQuery<T>($"...{value}...")` first: it supports unmapped result types and
  parameterizes interpolated **values**, with no new dependency and the same connection. Identifiers
  (table and column names) cannot be parameters; allowlist any dynamic identifier. Raw SQL must
  enforce tenant and soft-delete predicates itself.
- Add Dapper only for what that cannot give (multi-mapping, multiple result sets, a non-EF connection),
  and say so in the decision.
- It stays a thin I/O shell behind a port (reference 02); test it against the real engine
  (reference 12).

### Writes (`POST`, `PUT`, `PATCH`, `DELETE`)

**Default: EF Core with change tracking**, one `SaveChanges` per request. Use it for single-entity and
small-aggregate writes, where the domain rules need the entity loaded.

- **`POST` create:** `Add` + `SaveChanges`; rely on a unique constraint, not a pre-check alone; answer
  `201` with `Location`. If clients retry, take an idempotency key and implement it properly: atomic
  deduplication, a request fingerprint, the stored outcome and a retention period. Accepting the
  header alone is not idempotency.
- **`POST` receive-and-process** (a partner sends data to be processed later): do **not** write the
  business tables in the request. Validate, normalize, enqueue and answer `202` (reference 08).
- **`POST` batch:** small batches can use `AddRange` + `SaveChanges`. Reserve `EFCore.BulkExtensions`
  for volumes that justify it, with the four rules in reference 04, tested against the real engine
  (reference 12), not only SQLite.
- **`PUT` (full replace):** load, set every client-writable replacement field, `SaveChanges`; or one
  `ExecuteUpdate` when there are no domain rules. Idempotent by definition (responses need not be
  identical). Use a concurrency token (`rowversion` is SQL Server specific; other engines have their
  own) and map a failed `If-Match` to `412`, or an application-level conflict with no HTTP
  precondition to `409`, through `ProblemDetails`.
- **`PATCH` (partial):** change only the fields sent, tell an omitted field from an explicit `null`,
  and allowlist the writable properties. A set-based `ExecuteUpdate` for plain column changes with no
  domain rules (no load); load-and-modify when rules apply. Do not add JSON Patch unless the client
  really needs it. Same concurrency token as `PUT`.
- **`ExecuteUpdate` / `ExecuteDelete` caveats:** they run immediately, bypass change tracking and
  `SaveChanges` interceptors, and do not enforce concurrency tokens by themselves. Put the token the
  client read earlier in the `Where` (a freshly loaded `rowversion` does not protect against a stale
  client edit), check the affected-row count and handle zero rows explicitly. Tracked entities in the
  context become stale, and each call is its own round trip.
- **`DELETE`:** decide hard or soft and write the reason. Audit, retention and foreign keys call for
  an explicit deletion policy, which is not always soft delete. Soft delete means setting `IsDeleted`
  yourself; a global query filter only hides flagged rows and does not turn a delete into an update.
  `ExecuteDelete` physically deletes, even with that filter configured. Make it idempotent and
  document whether a repeat answers `204` or `404`.

**Raw SQL / Dapper for writes** only for a set-based operation EF Core cannot express, or an existing
stored procedure, through `ExecuteSql`/`ExecuteSqlInterpolated` first (non-query commands, same value
parameterization). As kit policy, avoid two competing writers on the same table: coordinated
persistence paths are fine, competing ownership is not. Never Dapper for writes "because it is
faster" with no measurement.

### CQRS — only when the two sides truly diverge

Choose full CQRS (a separate read model/store, own projections) only if at least one holds: reads need
a denormalized or different store; or read and write scale differently. Event sourcing and accepting
eventual consistency are considerations, not sufficient reasons on their own, and CQRS can share one
database.

- Having both `GET` and `POST` is **not** a reason. Handlers per feature already keep commands and
  queries apart in code (reference 05).
- This kit does not adopt a mediator library (MediatR or similar); handlers are called by interface.
- CQRS adds a second model to keep in sync. State that cost.

## Step 4 — Tell the developer the plan before implementing

Reply with this block. Wait for a veto only when the choice is not the default.

```
Operation: <VERB /route> - <what it does>
Plan:      <layers and files you will touch: endpoint, handler, validator, port, Context, tests>
Contract:  <status codes: success and the errors, ProblemDetails cases>
Decision:  <EF Core | EF Core ExecuteUpdate/ExecuteDelete | BulkExtensions | enqueue + 202 | SqlQuery<T> | Dapper | CQRS>
Why:       <1-3 reasons tied to the facts>
Rejected:  <the other options, one line each, why not>
Cost:      <new dependency, extra model, concurrency token, test against the real engine, ...>
Cache:     <only if relevant: verdict and where, per reference 17; otherwise "none">
Assumed:   <facts not confirmed, e.g. "under ~10k rows, no latency target, no concurrent updates">
```

If the developer brings numbers that contradict an assumption, revisit the decision. A performance
claim without a measurement is an assumption; label it as one.

## Anti-patterns

- Choosing the approach silently, so nobody can review why.
- Dapper "because it is faster" with no measurement.
- Returning EF entities from an endpoint, or a tracked query on a read.
- Loading an entity just to flip one column when a set-based `ExecuteUpdate` is enough, or the reverse:
  `ExecuteUpdate` where domain rules needed the entity.
- `ExecuteUpdate`/`ExecuteDelete` with no concurrency token in the `Where` and no affected-row check.
- `PUT`/`PATCH` with no concurrency token on data two clients can edit.
- An idempotency header that is accepted but not deduplicated.
- Hard-deleting rows that are audited or referenced without an explicit deletion policy.
- Writing business tables inside an ingestion endpoint that should only enqueue.
- A generic `IRepository<T>`, or a mediator added "for CQRS" on a service with simple operations.
- Two competing data-access libraries owning writes to the same table (kit policy).
- String-concatenated SQL.
