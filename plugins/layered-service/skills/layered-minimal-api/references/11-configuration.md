# 11 — Configuration

## The rule

Typed sections with the Options pattern and `ValidateOnStart`. Secrets never in a committed
`appsettings.json`. Local development uses `dotnet user-secrets` and a local queue emulator. The
business time zone is a **fixed offset**, never the operating system's.

Why `ValidateOnStart`: without it a missing or malformed setting is discovered by the first request
that needs it — in production, at 3am, as a 500. With it, the service refuses to start with a message
naming the setting, which a platform can act on immediately.

## Typed options

```csharp
namespace Contoso.Orders.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>Client id of the expected app registration (claims "azp"/"appid"). Optional.</summary>
    public string? RequiredClientId { get; set; }
}
```

```csharp
// Program.cs
builder.Services.AddOptions<SqlOptions>()
    .Bind(builder.Configuration.GetSection(SqlOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Sql:ConnectionString is required.")
    .ValidateOnStart();
```

Keep one options class per integration, with a `SectionName` constant used both for binding and for
reading. Never index configuration with `configuration["Section:Key"]` scattered across files — a typo
becomes a `null` that flows into a client constructor.

`SqlOptions` is the one exception worth noting: it is bound inside the `AddDbContextFactory` callback
through `IOptions<SqlOptions>` so the value is resolved from the built container, not captured at
registration time.

## appsettings files

```
appsettings.json                  committed: structure, non-secret defaults, empty secret values
appsettings.Development.json      NOT committed (gitignored) - local overrides
appsettings.QA.json               committed structure, secrets injected by the platform
appsettings.Production.json       committed structure, secrets injected by the platform
```

Committed file with empty secrets, so the shape is visible and reviewable:

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Sql": { "ConnectionString": "" },
  "Queue": { "ConnectionString": "", "OrdersQueueName": "orders-queue" },
  "Jwt": { "Authority": "", "Audience": "", "RequiredClientId": "" },
  "Carrier": { "Server": "", "Database": "", "UserName": "", "Password": "", "DirectoryMinutes": 15 },
  "RateLimit": { "MaxRequests": 1000, "Window": "00:01:00" },
  "OpenApi": { "Enabled": false },
  "ApplicationInsights": { "ConnectionString": "" }
}
```

`OpenApi.Enabled` is `false` in the committed file. Turning the API surface on is an environment
decision, not a default.

## Local development

```bash
cd src/Contoso.Orders.Api
dotnet user-secrets init
dotnet user-secrets set "Sql:ConnectionString" "Server=localhost;Database=orders;..."
dotnet user-secrets set "Queue:ConnectionString" "UseDevelopmentStorage=true"
dotnet user-secrets set "Jwt:Authority" "https://login.example.com/<tenant>/v2.0"
dotnet user-secrets set "Jwt:Audience" "api://contoso-orders"
```

`UseDevelopmentStorage=true` is the well-known connection string for the local storage emulator, so
queue code runs unmodified against a local instance. The equivalent database connection string is the
one your local engine expects; check the emulator's documentation rather than guessing.

`appsettings.Development.json` belongs in `.gitignore`. A developer who puts a real connection string
there and commits it has leaked a credential, and the file is committed by default in most templates —
so the ignore rule is part of the scaffold, not an afterthought.

## Secret hygiene

- Nothing secret in a committed `appsettings*.json`: no connection strings, no signing keys, no
  passwords, no client secrets, no tenant-specific URLs.
- In the cloud: platform-injected environment variables or a secret store with managed identity.
- Locally: `dotnet user-secrets`.
- A secret that has been committed is compromised. Rotate it; removing the line does not un-leak it.
- Use **Microsoft Entra ID** authentication for the database rather than a user/password pair, so
  rotating a password is never a deployment task.
- `.gitignore` also covers `*.user`, `*.log`, `.env`, `secrets.json`.

## Business time zone

```csharp
public static class BusinessTime
{
    // Fixed offset for the business time zone (e.g. UTC-05:00). Never the OS zone, and never a
    // DST-aware zone id: the offset is a business rule, not a property of the host.
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public static DateTimeOffset ParseLocal(string value, string format) =>
        new(DateTime.ParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None), Offset);

    public static DateTimeOffset ToBusiness(DateTimeOffset instant) =>
        instant.ToOffset(Offset).AddTicks(-(instant.ToOffset(Offset).Ticks % TimeSpan.TicksPerSecond));
}
```

Why this is worth its own section: parsing with the server's zone makes the same input produce a
different instant in a developer laptop and in the production region, and it only shows up as a
one-hour discrepancy in a report. The offset is a business fact; write it as a constant with a comment
explaining where it comes from.

Store and transmit timestamps in UTC; convert for display at the edges only.

## Naming

Types in English, always. Properties may follow the upstream system's or the database's language —
**pick one per repo and write it in `CLAUDE.md`**. A half-translated model is worse than either,
because nobody can search for it. Whichever you pick, HTTP route names stay in English: they are a
public contract with clients you do not control.

## Anti-patterns

- Secrets in a committed `appsettings*.json`, including "just the development" ones.
- `configuration["Section:Key"]` read in scattered files instead of typed options.
- No `ValidateOnStart`, so a missing setting surfaces as a 500 at request time.
- `appsettings.Development.json` not gitignored.
- `DateTime.Now` / `DateTime.Parse` with the OS zone for a business timestamp.
- `TimeZoneInfo.FindSystemTimeZoneById` for a business offset — a DST rule change then silently moves
  historical data.
- A user/password connection string where managed identity is available.
- `builder.Configuration.GetConnectionString(...)` inline in `Program.cs` with no options validation.
- Storing business-local times in the database instead of UTC.
- Different naming languages for the same concept across layers.
