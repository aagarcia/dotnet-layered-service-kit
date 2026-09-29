// Entry point of the presentation layer. The order below is not cosmetic: each step depends on
// the previous one having been configured.
//
// `Contoso.Orders` is the example namespace - replace it with `<Company>.<Product>` before building.
// Top-level statements live in the global namespace, so anything the entry point touches by name
// needs its namespace imported here - including types declared in this project's own Api namespace.
using System.Globalization;
using Contoso.Orders.Api;
using Contoso.Orders.Api.Endpoints;
using Contoso.Orders.Api.Observability;
using Contoso.Orders.Api.RateLimiting;
using Contoso.Orders.Api.Security;
using Contoso.Orders.Api.Services;
using Contoso.Orders.Application.UseCases;
using Contoso.Orders.Application.UseCases.IServices;
using Contoso.Orders.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Telemetry first, so everything registered after it is instrumented.
builder.Services.AddObservability(builder.Configuration);

// 2. Layers. Persistence before Application: the DbContext factory and the audit services are
//    injected into handlers.
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddApplicationServices();

// 3. Ports whose implementations live in this layer.
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton<IQueueService, QueueService>();

// 4. Authentication. Authority + Audience is the whole configuration: OIDC discovery, key
//    rotation and clock skew are handled by the framework. Never write a JWT validator.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];
        options.Audience = jwt["Audience"];
    });

// Deny by default: every route requires a valid token unless it opts out with .AllowAnonymous().
// A custom policy is only for what the framework cannot express - here, "issued to this client",
// which is what preserves the 401 (not authenticated) vs 403 (wrong caller) distinction.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(ClientPolicy.Name, policy =>
        policy.Requirements.Add(new RequiredClientIdRequirement(jwt["RequiredClientId"])));
builder.Services.AddSingleton<IAuthorizationHandler, RequiredClientIdHandler>();

// 5. Rate limiting. Per instance, not distributed - say so in docs/ARCHITECTURE.md.
//    RateLimitSettings is OUR options class, deliberately NOT named RateLimiterOptions: that name
//    belongs to Microsoft.AspNetCore.RateLimiting.RateLimiterOptions (pitfall 12).
var rateLimit = builder.Configuration.GetSection(RateLimitSettings.SectionName);
builder.Services.Configure<RateLimitSettings>(rateLimit);
var rateLimitSettings = rateLimit.Get<RateLimitSettings>() ?? new RateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter =
            ((int)rateLimitSettings.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };

    options.AddPolicy(OrderIngestRateLimiterPolicy.PolicyName,
        _ => OrderIngestRateLimiterPolicy.CreatePartition(rateLimitSettings));
});

// 6. ProblemDetails and the global exception handler.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks();

// 7. OpenAPI + Scalar only where they are enabled. Off by default, so production is not the
//    first place the API surface is published.
var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", false);
if (openApiEnabled)
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

// 8. Pipeline, outermost first. Exactly ONE UseRateLimiter() call: every call adds another
//    RateLimitingMiddleware, and each instance re-applies the global limiter *and* the endpoint
//    policy, so two calls double-charge the global budget and run the claims-based policy before
//    authentication. This template's policy partitions GLOBALLY, so the single call sits here,
//    before authentication. If you switch to a per-client (azp/appid) partition, MOVE this call to
//    after UseAuthentication(); for both budgets, configure a per-IP GlobalLimiter plus a
//    per-client endpoint policy on the same AddRateLimiter options. Full table: references/07.
app.UseExceptionHandler();
app.UseRateLimiter();

if (openApiEnabled)
{
    app.MapOpenApi();                     // /openapi/v1.json
    app.MapScalarApiReference();          // /scalar/v1
}

app.UseAuthentication();
app.UseAuthorization();

// 9. Endpoints: one MapGroup + Map<Module>Endpoints() per module.
//    No AddCors/UseCors: this is a server-to-server API. No AddControllers: minimal APIs only.
app.MapGroup("/orders").MapOrderEndpoints();
app.MapHealthEndpoints();

await app.RunAsync();

// Reachable by WebApplicationFactory<Program> from the test project.
// S1118 ("utility class should be static or have a protected constructor") is a false positive on
// this marker: the compiler generates the class for the top-level statements above, and it cannot
// be made static without breaking WebApplicationFactory<Program>. Suppressed here, on purpose.
#pragma warning disable S1118
public partial class Program;
#pragma warning restore S1118
