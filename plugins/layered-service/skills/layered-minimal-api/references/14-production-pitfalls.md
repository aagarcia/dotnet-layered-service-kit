# 14 — Production pitfalls

Every entry is a real failure that cost real time. Read the whole table before merging anything that
touches a bulk write, a queue, a third-party SDK or a background service.

**Symptom → Cause → Fix.** The theme of half of these is the same lesson: *the test suite passed and
production still broke.* Know which of your tests are lying to you.

---

## 1. Bulk upsert creates a real table in your schema

| | |
|---|---|
| **Symptom** | The bulk write fails with a permissions error that mentions nothing about permissions — or it works on the developer's machine and fails with the application's login on the first real deployment. |
| **Cause** | `EFCore.BulkExtensions` without `UseTempDB = true` creates a **staging table with a real name in your schema** and drops it, on every batch. That needs `CREATE TABLE` and `DROP TABLE` on the target schema. A low-privilege login has neither. |
| **Fix** | Always `new BulkConfig { UseTempDB = true }`. It stages into a temp table that is discarded with the connection. |
| **Detect** | A login that can `SELECT`/`INSERT`/`UPDATE` but not `CREATE TABLE`; anything that only shows up against the real database. |

```csharp
await db.BulkInsertOrUpdateAsync(rows, new BulkConfig { UpdateByProperties = [key], UseTempDB = true },
    cancellationToken: cancellationToken);
```

## 2. `UseTempDB = true` requires an explicit transaction

| | |
|---|---|
| **Symptom** | `InvalidOperationException` from the bulk library, immediately, on a real SQL Server. |
| **Cause** | The temporary-table staging is scoped to the transaction. Without one there is nothing to stage into. |
| **Fix** | Open the transaction explicitly around the bulk call. This is a hard requirement, not a style preference. |
| **Detect** | Only on a real SQL Server. SQLite does not reproduce it. |

## 3. A retry execution strategy and a hand-started transaction are mutually exclusive

| | |
|---|---|
| **Symptom** | `InvalidOperationException` the instant you call `BeginTransactionAsync()`: *"The configured execution strategy 'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions. Use the execution strategy returned by 'DbContext.Database.CreateExecutionStrategy()' to execute all the operations in the transaction as a retriable unit."* |
| **Cause** | With `EnableRetryOnFailure` configured, the context uses a retrying execution strategy. User-initiated transactions outside that strategy are rejected by design — otherwise a retry would silently run outside the transaction. |
| **Fix** | Let the strategy own the transaction: `db.Database.CreateExecutionStrategy().ExecuteAsync(async () => { ... })`, and open the transaction **inside** it. |
| **Detect** | Only when `EnableRetryOnFailure` is on and only on a real SQL Server. |

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    // ... bulk write, detail inserts, SaveChanges ...
    await transaction.CommitAsync(cancellationToken);
});
```

## 4. None of 1–3 reproduce on SQLite

| | |
|---|---|
| **Symptom** | 400 green tests, and the first production deployment fails on the very first batch write. |
| **Cause** | SQLite is a real SQL engine for queries, but it has no `tempdb` bulk-staging path, no SQL Server execution strategy, and a different transaction model. The three most dangerous behaviours in the persistence layer are exactly the ones the test database cannot express. |
| **Fix** | Keep SQLite in-memory for fast unit tests. Then **verify against a real SQL Server before shipping**: run the batch, and watch the rows actually move and the transaction actually commit. Treat it as a manual integration step in `docs/DEPLOYMENT.md`. |

## 5. A bounded channel can lose data silently

| | |
|---|---|
| **Symptom** | Records are missing and there is no error, no log line and no counter. Best-effort audit rows simply never appear. |
| **Cause** | With `BoundedChannelFullMode.DropWrite`, `TryWrite` returns **`true` even when it discarded the item**. The drop is indistinguishable from success. |
| **Fix** | Use `FullMode = BoundedChannelFullMode.Wait`. `TryWrite` then returns `false` without blocking when the channel is full — and `false` is observable. |
| **Detect** | Compare the input count to the persisted count under sustained load. The counter with a `reason` tag is the second line of defence. |

```csharp
public void TryEnqueue(AuditLogEntry entry)
{
    if (!channel.Writer.TryWrite(entry))
        OrderTelemetry.AuditErrors.Add(1, new KeyValuePair<string, object?>("reason", "channel_full"));
}
```

## 6. A scoped service wrapping shared state breaks it

| | |
|---|---|
| **Symptom** | The writer processes nothing. No errors, no log lines, the channel is always empty — and the producer "knows" it wrote. |
| **Cause** | The wrapper around a singleton channel was registered `Scoped`. Each request resolves its own instance, so the writer's reader and the producer's writer are not the same object, or the per-request state resets every time. |
| **Fix** | Register the wrapper **Singleton**, and check that everything consuming it is also singleton-compatible. A `BackgroundService` is a singleton by nature — anything it injects must be too. |
| **Detect** | Register the concrete type and resolve the interface from the same registration, so both resolve the same instance. |

## 7. `CreateIfNotExistsAsync` per request

| | |
|---|---|
| **Symptom** | Outbound request doubling to the queue, throttling under load, p95 latency climbing while the payload stays small. |
| **Cause** | `CreateIfNotExistsAsync` on every send is an extra round trip per message. The queue almost always already exists, so it is pure overhead — and under load it becomes throttling. |
| **Fix** | Create the queue **once**: at startup in an `IHostedService`, or on first use behind a `SemaphoreSlim` with a `volatile bool` flag. Never in the hot path. |
| **Detect** | Count HTTP calls to the storage service per ingested message. It should be one. |

## 8. A column with a SQL `DEFAULT` is written as `0001-01-01`

| | |
|---|---|
| **Symptom** | A timestamp column is full of `0001-01-01`, or counts are wrong because the "default" never applied. |
| **Cause** | EF sends the CLR default value for every column in the insert, and bulk libraries include every column. A `DEFAULT` clause only fires for a hand-written `INSERT` that omits the column. |
| **Fix** | Declare it in the entity configuration: `ValueGeneratedOnAdd()` plus `HasDefaultValueSql(...)` (or `HasDefaultValue(...)`). Set the value in code for the insert and do not overwrite it on update. |
| **Detect** | Query for the sentinel `0001-01-01` in production. It should return zero rows. |

## 9. AOT and trimming break reflection-based SDKs

| | |
|---|---|
| **Symptom** | `System.NotSupportedException: Reflection-based serialization has been disabled`. Builds and publishes cleanly; throws on the first call that serializes. |
| **Cause** | `PublishAot`/`PublishTrimmed` removes the reflection machinery a vendor SDK depends on. |
| **Fix** | Do not enable AOT or trimming in a service that uses such an SDK. If the performance target requires it, choose a different SDK or add explicit `DynamicDependency`/source-generator support. |
| **Detect** | Only at runtime, on the first SDK call. AOT is not covered by the test suite. |

## 10. A session-holding SDK, constructed per request

| | |
|---|---|
| **Symptom** | Socket exhaustion, duplicated logins, duplicated connections against the provider, and eventually provider-side throttling that looks like your own bug. |
| **Cause** | The SDK keeps a session and authenticates on demand. Constructing it per request creates a session per request. Passing it an `HttpMessageHandler` from `IHttpClientFactory` is worse: the SDK retains it forever, so nothing is ever rotated. |
| **Fix** | One **singleton** instance, its own process-lifetime `SocketsHttpHandler`, and a `SemaphoreSlim` around the first login (the SDK has no auth lock of its own, so N concurrent first requests fire N logins). |
| **Detect** | Count logins per N requests. It should be 1 per process, plus one per session expiry. |

## 11. Parsing with the operating system's time zone

| | |
|---|---|
| **Symptom** | Timestamps are off by an hour — but only in one environment. Dev is right, production is wrong, or the same input produces different instants in different regions. |
| **Cause** | `DateTime.Parse` / `DateTime.Now` use the host's zone, which changes with the region the process runs in and with daylight-saving rules. |
| **Fix** | A **fixed business offset** constant, used in every parse and every conversion, with a comment saying where the offset comes from. Store UTC; convert only for display. |
| **Detect** | Set the container's `TZ` to something else and re-run the tests. Any behaviour change is the bug. |

## 12. A class name that collides with a BCL type

| | |
|---|---|
| **Symptom** | A confusing compile error about a delegate not matching a method signature, on a class that looks correct. |
| **Cause** | A type named after something in the BCL — most commonly `EventHandler` — collides with the implicitly imported `System` namespace (`ImplicitUsings`) or with an implicit type. |
| **Fix** | Rename the type, or fully qualify it at the use site. Renaming is better: the collision will return the next time someone removes the qualification. |
| **Detect** | Grep for your own type names that also exist in `System` before merging. |

## 13. CORS added "just for testing" ends up in production

| | |
|---|---|
| **Symptom** | A permissive CORS policy in a server-to-server API, added for a local experiment, reviewed by nobody. |
| **Cause** | CORS is a browser mechanism. Server-to-server callers ignore it, so it feels free to add "for the tests" — and then it is the only thing standing between the API and a browser-based attack surface. |
| **Fix** | No CORS in a server-to-server API. Test with `WebApplicationFactory`, which does not need it. If a browser is a genuine client, use a named policy with an explicit origin list. |
| **Detect** | `grep -ri "AddCors\|UseCors" src/` should return nothing in a server-to-server service. |

## 14. A committed `appsettings.Development.json` leaks credentials

| | |
|---|---|
| **Symptom** | A real connection string, a signing key or a password in the repository history. |
| **Cause** | The file is committed by default in most templates, so one `git add .` publishes it. Later deletions do not un-leak it. |
| **Fix** | Gitignore it. Use `dotnet user-secrets` locally, platform-injected variables in the cloud. **Rotate anything that was committed** — removing the line is not remediation. |
| **Detect** | Scan history, not just the current tree, before ever making a repository public. |

## 15. Coverage that silently produces nothing

| | |
|---|---|
| **Symptom** | `dotnet test --collect:"XPlat Code Coverage"` completes successfully, and no coverage file appears. |
| **Cause** | The project runs on Microsoft.Testing.Platform, which does not host VSTest extensions. `coverlet.collector` is a VSTest collector: it loads, does nothing, and exits 0. |
| **Fix** | Use `Microsoft.Testing.Extensions.CodeCoverage` and `dotnet test --coverage`. Remove the vestigial `coverlet.collector` reference so nobody assumes it works. |
| **Detect** | Assert that the output file exists after a coverage run. |

## 16. A new provider is not a copy of an existing one

| | |
|---|---|
| **Symptom** | The second integration inherits the first one's flow — its retry policy, its cache TTL, its push/pull shape — and every one of those assumptions is wrong for the new provider. |
| **Cause** | Copy-paste beats design when the previous integration is right there and already working. |
| **Fix** | Reuse the **structure and the conventions** — the folder layout, the port, the error-translator shape, the telemetry names, the lifecycle rules. Design the **flow** from the provider's nature: a push provider wants a receiver, a poll-only provider wants a refresher, a stateless REST provider wants nothing cached. Then decompile the SDK and read its actual limits. |
| **Detect** | Ask: "what does this provider's API actually permit?" If the answer is a copy of the other provider's design, the design is wrong. |

## 17. A background service that crashes takes the whole host with it

| | |
|---|---|
| **Symptom** | A transient failure - the database is briefly unreachable - takes down the **entire API**. The process exits, the orchestrator restarts it, and whatever requests were in flight fail with a connection reset. A `BackgroundService` that was only ever a *best-effort* helper is enough to cause a full outage. |
| **Cause** | Since .NET 6 the default is `BackgroundServiceExceptionBehavior.StopHost`, so an exception escaping `ExecuteAsync` stops the **host**, not just that one service. Nothing restarts the service in-process, and the crash is attributed to the host while the actual fault is in a helper nobody was watching. It is only silent if someone explicitly set `HostOptions.BackgroundServiceExceptionBehavior = Ignore`. |
| **Fix** | Catch inside the loop, count the failure, and keep going: `ExecuteAsync` must never let an exception escape, because the default behaviour turns one into a host shutdown. The **flush callback** is the single place that rethrows, and there it must log and count **before** rethrowing - the items have already left the buffer and the timer callback has nobody else to tell. **Do not "fix" this by switching the behavior to `Ignore`** - that does not make the failure survivable, it just stops logging it. |
| **Detect** | Alert on the error counter for the writer, and treat a `StopHost` log line as a full outage, not a background warning. |

## 18. Shutdown loses the buffered work

| | |
|---|---|
| **Symptom** | The last few hundred audit rows before a deploy are missing. Never during the day, always at shutdown. |
| **Cause** | The flush timer is periodic; a deploy between ticks discards whatever is in the buffer. |
| **Fix** | Override `StopAsync`, flush, then call the base implementation. The accumulator's `DisposeAsync` should flush as its last act. |
| **Detect** | Deploy on a loop and compare the last timestamp before shutdown with the deployment time. |

## 19. An unbounded queue

| | |
|---|---|
| **Symptom** | Memory climbs until the process is recycled, then the backlog is gone. |
| **Cause** | A channel with no bound accepts everything, so a slow database or a fast producer becomes an out-of-memory condition. |
| **Fix** | `Channel.CreateBounded` with a size chosen from the traffic you can survive losing, and count every drop. An unbounded channel is only acceptable when the producer is throttled by something else. |
| **Detect** | Alert on the `reason = channel_full` counter. It is a capacity problem you get told about instead of discovering. |

## 20. `Task.Run` around already-async code

| | |
|---|---|
| **Symptom** | Thread-pool exhaustion under load, with plenty of memory free. |
| **Cause** | Wrapping `async` work in `Task.Run` queues it behind the thread pool instead of starting it, adding a scheduling hop and consuming a pool thread. It also silently makes the call fire-and-forget when the task is discarded, losing exceptions. |
| **Fix** | Call the async method and `await` it. Use `Task.Run` only for genuinely CPU-bound work that has no async equivalent. |
| **Detect** | `Task.Run` appearing in a request path, or a discarded task. |

## 21. `UseRateLimiter()` called twice to get two limits

| | |
|---|---|
| **Symptom** | The configured limits are not the effective ones. A limit set to 100/min rejects at ~50/min, and a per-client policy that was written to be fair turns out to be shared by every caller. The behaviour looks non-deterministic, which is the giveaway. |
| **Cause** | Each `UseRateLimiter()` adds another `RateLimitingMiddleware` instance, and every instance reads the *same* `RateLimiterOptions`: it charges `GlobalLimiter` **and** the endpoint's own policy. Two calls therefore charge the `GlobalLimiter` twice per request — the real ceiling is half of what you configured — and evaluate the claims-based policy twice, the first time before `UseAuthentication()`, when `HttpContext.User` has no claims, so every caller lands in the same partition. The second call does not "add" the per-client limit; it re-runs both. |
| **Fix** | Call `UseRateLimiter()` **once**, after `UseAuthentication()` if any policy partitions by `azp`/`appid`. For two budgets, configure both tiers on the same options: a per-IP `options.GlobalLimiter` plus a per-client endpoint policy, with that one middleware charging both. Flood protection *before* token validation belongs at the edge (WAF / API gateway), not in a second middleware — JWT validation does no per-request I/O because OIDC keys are cached. |
| **Detect** | `grep -c "UseRateLimiter()" src/` returns more than 1; or a global limit observed at half its configured value. |

---

## Before you merge

Walk this list explicitly. Do not trust that you avoided them by instinct.

- [ ] No bulk write without `UseTempDB = true`, with an explicit transaction, wrapped in the execution strategy — and verified against a real SQL Server.
- [ ] Every SQL `DEFAULT` column has `ValueGeneratedOnAdd` / `HasDefaultValueSql`.
- [ ] Every `Channel<T>` is bounded, `FullMode = Wait`, and every drop is counted with a `reason`.
- [ ] Every service wrapping a channel, a cache or shared state is `Singleton`, and so is everything a `BackgroundService` injects.
- [ ] No `CreateIfNotExistsAsync` in a request path.
- [ ] Every `BackgroundService` absorbs its own exceptions, counts them, and flushes on `StopAsync`; none relies on `BackgroundServiceExceptionBehavior = Ignore` to stay alive.
- [ ] Every session-holding SDK is a single singleton with its own handler and a login lock; AOT/trimming off.
- [ ] Every timestamp is parsed with the fixed business offset.
- [ ] Coverage actually produces a file.
- [ ] No secrets in any committed `appsettings*.json`; `appsettings.Development.json` gitignored.
- [ ] No CORS in a server-to-server API.
- [ ] `UseRateLimiter()` is called exactly once, positioned after `UseAuthentication()` if any policy partitions by a claim.
- [ ] `dotnet build` and `dotnet test` are clean, zero warnings.
