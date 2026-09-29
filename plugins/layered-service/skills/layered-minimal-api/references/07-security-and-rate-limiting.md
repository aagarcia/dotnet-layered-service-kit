# 07 — Security and rate limiting

## JWT: use the framework, always

```csharp
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwt);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];   // OIDC discovery is automatic
        options.Audience = jwt["Audience"];
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
```

`Authority` + `Audience` is the whole configuration. The middleware fetches the signing keys from the
identity provider's discovery document and caches them, refreshes expired tokens, and answers `401`
by itself. **Never write your own JWT validator.** A hand-rolled one is missing key rotation, clock
skew, audience checking, and algorithm-confusion defence, and none of those are visible in review.

Typical shape: machine-to-machine tokens issued with the `client_credentials` flow.

```json
{ "Jwt": { "Authority": "https://login.example.com/<tenant>/v2.0", "Audience": "api://contoso-orders" } }
```

## Deny by default

`SetFallbackPolicy(RequireAuthenticatedUser())` means every route requires a valid token unless it
opts out with `.AllowAnonymous()`. Do not chain `.RequireAuthorization()` group by group; the default
is already "denied", so an endpoint added later is secure by accident rather than by oversight.

`.AllowAnonymous()` belongs on exactly two things: the health endpoint, and a login endpoint **if this
API is itself the token issuer**.

## If the API issues tokens

Most service-to-service APIs do not issue tokens; they validate tokens issued elsewhere. Only when the
API really is the issuer, emit the token from an endpoint:

- the interface `IJwtTokenGenerator` lives in `Application.UseCases/IServices/` (so handlers can
  depend on it without that layer referencing a JWT package);
- the implementation, which needs `System.IdentityModel.Tokens.Jwt`, lives in `Api/Services/`;
- the endpoint is `.AllowAnonymous()` and returns the token in the body — no cookie.

Keep the signing key out of committed `appsettings.json` (see `references/11-configuration.md`).

## Custom policies: only what the framework does not have

ASP.NET Core can express "authenticated". It cannot express "authenticated **and** issued to this
specific client". That gap is the only legitimate reason for custom authorization code — the 401 vs 403
split.

```csharp
namespace Contoso.Orders.Api.Security;

/// <summary>
/// Requires that the already-validated token came from the expected app registration
/// (claims "azp"/"appid"), not from any other app in the tenant holding a token for our
/// audience. Fails as 403, never 401: the token authenticated, it is just the wrong caller.
/// </summary>
public sealed class RequiredClientIdRequirement(string? requiredClientId) : IAuthorizationRequirement
{
    public string? RequiredClientId { get; } = requiredClientId;
}
```

```csharp
public sealed class RequiredClientIdHandler(ILogger<RequiredClientIdHandler> logger)
    : AuthorizationHandler<RequiredClientIdRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, RequiredClientIdRequirement requirement)
    {
        if (string.IsNullOrEmpty(requirement.RequiredClientId))
        {
            context.Succeed(requirement);   // not configured: policy is a no-op, by design
            return Task.CompletedTask;
        }

        var azp = context.User.FindFirst("azp")?.Value;
        var appid = context.User.FindFirst("appid")?.Value;

        if (string.Equals(azp, requirement.RequiredClientId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(appid, requirement.RequiredClientId, StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
        else
        {
            // Log the reason, never the token: the token is a credential and belongs in nobody's logs.
            logger.LogWarning(
                "Valid token from an unauthorized client (expected: {RequiredClientId}).",
                requirement.RequiredClientId);
        }

        return Task.CompletedTask;
    }
}
```

On authentication failure, log the reason through `JwtBearerEvents.OnAuthenticationFailed`. Log the
event, the exception and the failure kind — never the raw token, never the `Authorization` header.

## Rate limiting: use the framework

```csharp
public static class OrderIngestRateLimiterPolicy
{
    public const string PolicyName = "OrderIngest";
    private const int SegmentsPerWindow = 4;

    public static RateLimitPartition<string> CreatePartition(RateLimitSettings settings) =>
        RateLimitPartition.GetSlidingWindowLimiter("global", _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = settings.MaxRequests,
            Window = settings.Window,
            SegmentsPerWindow = SegmentsPerWindow,
            QueueLimit = 0,      // reject immediately instead of queueing
        });
}
```

Notes:

- Put the policy in its own static class, not inline in `Program.cs`, so a unit test can assert the
  partition without booting a host.
- Partition by **client id** (the `azp`/`appid` claim) when clients are identifiable — a single global
  partition lets one noisy client starve the others. A global partition is fine only when the API has
  exactly one caller.
- A claims-based partition **cannot be built before authentication**. Before `UseAuthentication()` has
  run, `HttpContext.User` carries no claims, so every request lands in the same partition and the
  per-client budget silently degrades into a global one.
- `QueueLimit = 0` rejects; a positive queue limit converts overload into latency for everyone.
- Always answer `429` with `Retry-After`. It is a protocol requirement and clients rely on it.
- Give expensive third-party queries their **own** policy with their own budget, separate from the
  cache-backed ones, so a catalogue sweep cannot eat the budget of live queries.
- Rate limiting here is **per instance**, not distributed. Say so in the docs; a horizontally scaled
  deployment multiplies every limit by the instance count.

### Where `UseRateLimiter()` goes

**Exactly once.** Not once per limit you want to enforce. Every call adds another
`RateLimitingMiddleware` instance, and each instance independently applies **both** the
`GlobalLimiter` and the endpoint's own policy. Two calls therefore charge the global limiter twice
per request — the real ceiling is half of what you configured — *and* evaluate the claims-based policy
twice, the first time before authentication, when there are no claims to partition on and every caller
lands in one bucket.

| Configuration | Position of the single `UseRateLimiter()` |
|---|---|
| **Any** policy partitioned by a claim (`azp`/`appid`) | **After** `UseAuthentication()`, before `UseAuthorization()` — the partition key is a claim, and claims only exist once authentication has run |
| Every partition global or by IP | May sit **before** `UseAuthentication()` |

The rule is about the *configuration*, not about individual policies: one claims-based policy anywhere
in the service is enough to push the single call after authentication.

### Two budgets, one call

You do not need a second middleware for a second budget. Configure both tiers on the same
`RateLimiterOptions` and let the one middleware charge both. `settings` below is your own bound
options class, not the framework's `RateLimiterOptions`:

```csharp
builder.Services.AddRateLimiter(options =>
{
    // Tier 1: every request, keyed by IP.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext => RateLimitPartition.GetSlidingWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = settings.MaxRequestsPerIp,
                Window = settings.Window,
                QueueLimit = 0,
            }));

    // Tier 2: only on endpoints that declare it, keyed by the client id claim.
    options.AddPolicy(OrderIngestRateLimiterPolicy.PolicyName,
        _ => OrderIngestRateLimiterPolicy.CreatePartition(settings));
});
```

```csharp
app.UseAuthentication();
app.UseRateLimiter();          // the one call: charges the per-IP global tier and the per-client policy
app.UseAuthorization();
```

The endpoint policy applies only where an endpoint declares it, so the per-IP ceiling is the backstop
for everything and the per-client budget is the fairness guarantee among authenticated callers.

### Floods before token validation are the edge's job

Rejecting a volumetric flood *before* it reaches token validation is a WAF / API gateway / front door
responsibility, not a second `UseRateLimiter()`. It buys less than it looks: JWT validation performs no
per-request I/O, because the OIDC signing keys are fetched once and cached. A flood that is worth
rejecting at that depth is worth rejecting at the edge, where it costs you nothing.

## CORS

**Do not add CORS to a server-to-server API.** It is a browser mechanism; a server-to-server caller does
not read it. A CORS policy added "just for local testing" survives to production, then quietly becomes
the thing a reviewer sees as the security posture of the API. Test server-to-server paths with
`WebApplicationFactory`, which needs no CORS.

The only exception: a browser is a genuine client, in which case add a named policy with an explicit
origin list and no `AllowAnyOrigin` + `AllowCredentials` combination.

## Anti-patterns

- A hand-written JWT validator, an `IConfigurationManager<OpenIdConnectConfiguration>`, or reading the
  `Authorization` header by hand.
- `SymmetricSecurityKey` with a signing key in `appsettings.json` committed to the repository.
- Not marking the login endpoint `.AllowAnonymous()` and wondering why it returns 401 forever.
- A custom requirement that returns 401 for an authorization failure — 403 is the correct code, and
  conflating them breaks client retry logic.
- Logging the token, the header, or the full token payload in a rejection message.
- A custom `SlidingWindowRateLimiter` when `RateLimitPartition.GetSlidingWindowLimiter` exists.
- `429` without `Retry-After`, or without a ProblemDetails body.
- One global rate limit covering a cheap endpoint and an expensive third-party call.
- `AddCors(...).AllowAnyOrigin().AllowCredentials()` "for the tests".
- A claims-based partition with `UseRateLimiter()` placed before `UseAuthentication()` — there are no
  claims yet, so every caller shares one partition and the per-client budget is a fiction.
- `UseRateLimiter()` called more than once to get two limits. Every instance applies the
  `GlobalLimiter` *and* the endpoint policy, so the global budget is charged twice per request and the
  claims-based policy is evaluated once before authentication. Configure both tiers on one
  `RateLimiterOptions` instead. See pitfall 21.
- Hard-coding a client id or tenant id in code instead of configuration.
