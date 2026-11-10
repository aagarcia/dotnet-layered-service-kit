# 17 — Caching and Redis: when to use them, when not, and where

Redis is **available as an option** in this kit. It is never added by default. The developer decides
whether to use it and where; your job is to say what you recommend and why, and to tell the developer
plainly when the place they picked is not viable. Use this file together with reference 16, for any
endpoint where the developer mentions cache, Redis, speed, load, or where a read is repeated.

## Step 1 — Is a cache even justified?

A cache is a second copy of the data. It buys speed and costs correctness. Do not add one unless the
facts say so.

| Fact | Points to a cache | Points away from a cache |
|---|---|---|
| Read cost | Expensive query, slow upstream SDK call, heavy aggregate | Cheap indexed lookup the database already serves well |
| Read/write ratio | Read many times per change | Written about as often as read |
| Staleness | The caller can accept data that is seconds or minutes old | The caller must see the write immediately |
| Shape of the key | Same result for many callers (a catalogue, a reference list) | Result differs per user or tenant and the key space is huge |
| Evidence | A measurement shows the read is the bottleneck | "It might be slow" with no numbers |

If the facts point away, recommend **no cache** and say why. That is a valid, common answer.

## Step 2 — Which kind (smallest that works)

| Option | Use when | Notes |
|---|---|---|
| **No cache** | Default | Fix the query, add an index or a projection first (reference 16) |
| **In-memory, per instance** (`IMemoryCache`, or `HybridCache` with no distributed backend) | One instance, or staleness between instances is harmless | Fastest, no new infrastructure. Each instance holds its own copy |
| **`HybridCache` + Redis** (in-memory first level, Redis second level) | Several instances need a shared copy, or the copy should outlive an app restart (and Redis actually retains it) | Stampede protection works **within one process**: one caller loads, the others wait. It is not distributed across replicas. Prefer it to hand-rolled get/set |
| **`IDistributedCache` on Redis directly** | You need something `HybridCache` cannot give | `HybridCache` already supports custom serializers and caller-defined keys, so those alone do not justify it. More code, no stampede protection; justify it |
| **Output caching** (`AddOutputCache`) | A whole HTTP response is identical for all callers | Authenticated responses are excluded by default. Varying by header or query does not make caching them safe: overriding that default needs an explicit policy and trusted identity partitioning |

Whether Redis pays off is a **measurement and a requirement**, not a rule. Several instances or a
shared copy across processes are the usual reasons. A single instance can still benefit from external
memory capacity, but with one instance and no measurement Redis adds a network hop and a failure mode.
"Survives a restart" assumes Redis retains the data; it is not a durability guarantee.

## Step 3 — Where it goes (and where it does not)

- **Reads (`GET`):** cache-aside. Look up, on a miss load from the source and store with a TTL.
- **Writes (`POST`/`PUT`/`PATCH`/`DELETE`):** never write to the cache as the source of truth.
  Invalidate (remove) the affected keys **after the final database commit** succeeds, never before and
  not inside a transaction that has not committed yet. This includes `ExecuteUpdate`/`ExecuteDelete`
  (reference 16): they skip tracking, so nothing invalidates for you. See Step 4, rule 10.
- **Async ingestion (`202`):** do not cache. The endpoint enqueues (reference 08).
- **Idempotency keys:** Redis can hold the deduplication record, but `IDistributedCache` has no atomic
  set-if-absent. Use a Redis-specific API for an atomic `SET key value NX EX|PX`. Do **not** apply the
  fail-open read-cache behavior to deduplication: if Redis is down the endpoint must decide
  explicitly (reject, or fall back to a database unique constraint). An expiring claim alone does not
  make the database operation exactly-once; keep the unique constraint (reference 16).
- **Not here:** the domain layer, entity configuration, or inside a handler's business logic. The cache
  sits **behind a port** as a thin I/O shell (reference 02): a decorator around the reader, with the
  key building in pure, unit-testable classes.

## Step 4 — Rules for whatever you add

1. **Key design.** A version prefix (`orders:v1:...`), every component escaped or length-prefixed so a
   separator inside a value cannot make two different callers share a key, and a service/environment
   namespace if Redis is shared. Include the tenant or user whenever the result depends on them, and
   take that identity from the **authorized caller context**, never from a field the caller
   controls. A key that omits a dimension serves one caller another caller's data.
2. **TTL on every entry.** No entry without an expiry. Add jitter so many keys do not expire together
   (the templates do not; it is a required adaptation).
3. **Redis down must not take the API down.** `HybridCache` logs a Redis (L2) read failure and treats
   it as a miss, and logs and swallows an L2 write failure. Do not add a broad `catch` around
   `GetOrCreateAsync`: concurrent callers share one factory run, so you cannot tell a cache failure
   from a source failure with a caller-local flag. Set explicit short Redis timeouts and
   `AbortOnConnectFail = false`. Watch Redis through the backend and `HybridCache` logs and metrics.
4. **Authorization first.** Never serve a cached response before the caller is authorized, and never
   cache a result that depends on the caller without that caller in the key.
5. **No secrets or personal data in values** unless the data needs to be there; Redis is shared
   infrastructure.
6. **Bound it.** `MaximumPayloadBytes` caps one value, not total memory. Also bound the number of
   entries (required adaptation); do not cache unbounded lists or full tables.
7. **Observe it.** Count source loads in the factory and invalidation failures on a `Meter` registered
   in `Domain/Telemetry` (reference 10). Do not infer hit or miss from a caller-local flag. A cache
   nobody measures cannot be defended or removed.
8. **Configure it.** The Redis connection string is a secret. The options use `ValidateOnStart`
   (reference 11): positive TTLs, `LocalTtl <= DefaultTtl`, positive payload limit. Register the Redis
   backend only when a non-blank connection string exists; always register `HybridCache`.
9. **Test it.** Unit-test the key builder (including a value that contains a separator) and the
   invalidation logic with a fake cache. Verify the hit, miss and Redis-down paths against a real
   Redis instance, not only a fake (reference 12).
10. **Invalidation is best effort after the commit.** `RemoveAsync` can throw after the database
    already committed; log and count it, never fail the response or imply a rollback, and let the TTL
    bound the staleness. Where staleness is not acceptable, add a durable retry (outbox). Removing a
    key does not clear other instances' in-memory copies (they live until `LocalTtl`), and a read
    already in flight can repopulate the old value. Tenant-wide invalidation needs `RemoveByTagAsync`
    (logical expiry, not a physical delete). Post-commit invalidation is the right order; it is not
    immediate consistency.

## Step 5 — Give the developer a verdict before implementing

Add this block to the plan from reference 16 whenever a cache is on the table:

```
Cache:      <none | in-memory | HybridCache + Redis | IDistributedCache + Redis | output cache>
Verdict:    <Viable | Viable with conditions | Not recommended>
Where:      <the endpoint/port the developer chose, or the place you recommend instead>
Why:        <1-3 reasons from the facts in Step 1>
Key + TTL:  <key shape including tenant/user, TTL, jitter>
Invalidate: <which writes remove which keys, after the commit; failure policy>
Cost:       <new dependency, Redis in the failure path, staleness window, extra tests>
Developer:  <the decision is yours; this is the recommendation>
```

If the developer wants it somewhere you rate as **Not recommended**, say so once, with the reason and a
better place, then implement their choice with the rules above. The decision is theirs.

## Templates

Two copy-ready files, used only when the verdict says a cache is justified:

- `templates/CachingExtensions.cs` - `AddCaching(...)`: `HybridCache` always (in-memory), plus Redis as
  the second level only when `Cache:RedisConnectionString` is set. Validated options with
  `ValidateOnStart`, short Redis timeouts, `AbortOnConnectFail = false`. No connection string, no
  Redis, and the service still starts. Call it from `Program.cs` next to `AddObservability`.
- `templates/CachedOrderStatusReader.cs` - a decorator behind a port, a pure key builder (escaped
  components, version prefix, tenant tag), source-load counting in the factory, best-effort
  post-commit invalidation, and tenant-wide invalidation by tag.

Packages (Api project), see `references/15-checklists.md` (a2):

```bash
dotnet add src/Contoso.Orders.Api package Microsoft.Extensions.Caching.Hybrid
dotnet add src/Contoso.Orders.Api package Microsoft.Extensions.Caching.StackExchangeRedis   # only for the Redis second level
```

Configuration (the connection string is a secret: `dotnet user-secrets`, never committed):

```json
"Cache": { "DefaultTtl": "00:05:00", "LocalTtl": "00:00:30", "MaximumPayloadBytes": 1048576, "RedisTimeout": "00:00:00.500" }
```

Add the counters the decorator uses to your telemetry class (reference 03):
`CacheSourceLoads = Meter.CreateCounter<long>("cache.source.loads", description: "Loads that went to the source because the cache could not answer.")` and
`CacheInvalidationFailures = Meter.CreateCounter<long>("cache.invalidation.failures", description: "Post-commit cache invalidations that failed.")`.

Not implemented in the templates, so they are **required adaptation steps**: TTL jitter, an
entry-count bound, a durable invalidation retry, and a service/environment key namespace.

Verification status: the templates compile against `net10.0` with those two packages. They have **not**
been run against a live Redis; do that (hit, miss, Redis down) before relying on them (rule 9).

## Anti-patterns

- Adding Redis "for performance" with no measurement and a single instance.
- A cache key missing the tenant or user, or built by plain concatenation of unescaped values.
- Taking the tenant for the key from a request field the caller controls.
- Invalidating before the commit, or writing to the cache as if it were the database.
- Failing the HTTP response because post-commit invalidation threw.
- No TTL, or one TTL for everything.
- Redis as a hard dependency: the API errors when Redis is down.
- A broad `catch` around `GetOrCreateAsync` that retries the source, or hit/miss counted from a
  caller-local flag.
- Caching authenticated, per-user HTTP responses with output caching and no explicit policy.
- Hand-rolled get-then-set with no stampede protection on a hot key.
- Caching business logic results inside the domain or the handler instead of behind a port.
- Treating "cache unavailable or not yet loaded" as "no data". A legitimately cached empty or `null`
  source result is a valid answer; do not confuse the two (reference 09).
- Using `IDistributedCache` for an idempotency claim that needs an atomic `SET ... NX`.
