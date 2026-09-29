# 12 — Testing and quality

## The rule

xUnit v3 on Microsoft.Testing.Platform. Hand-written fakes for the ports. EF tests on **SQLite
in-memory**, never the EF `InMemory` provider. Coverage through
`Microsoft.Testing.Extensions.CodeCoverage`. Analyzer configured in `Directory.Build.props`. **Build with
zero warnings.**

## Test runner

```json
// global.json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <IsPackable>false</IsPackable>
</PropertyGroup>

<ItemGroup>
  <Using Include="Xunit" />
</ItemGroup>
```

With the platform runner, `coverlet.collector` + `--collect` **produces nothing** — it is a VSTest
extension and VSTest is not running:

```bash
dotnet test --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
```

That works because of `Microsoft.Testing.Extensions.CodeCoverage`. A vestigial `coverlet.collector`
reference in a test project is dead weight: remove it rather than leaving it to mislead the next
person.

## Fakes, not mocks, for ports

```csharp
public sealed class FakeQueueService : IQueueService
{
    public List<OrderMessage> Enqueued { get; } = [];

    public Task EnqueueOrderAsync(OrderMessage message, CancellationToken cancellationToken = default)
    {
        Enqueued.Add(message);
        return Task.CompletedTask;
    }
}
```

```csharp
public sealed class FakeAuditLogService : IAuditLogService
{
    public List<AuditLogEntry> Entries { get; } = [];

    public void TryEnqueue(AuditLogEntry entry) => Entries.Add(entry);
}
```

Why hand-written fakes: they are the public contract of the port made executable, they survive a
refactor of the call site, and they let a test assert on *what* was enqueued rather than on which
methods were called. Use a mocking library only when the collaborator is not yours.

Also worth having: a `FixedTimeProvider` for anything that stamps a timestamp.

## Pure logic: plain unit tests

```csharp
public sealed class OrderNormalizerTests
{
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Normalize_trims_and_uppercases_the_reference()
    {
        var result = OrderNormalizer.Normalize(new ReceivedOrder("  ord-77 ", [], null), Clock);

        Assert.Equal("ORD-77", result.Reference);
        Assert.Equal(Clock.GetUtcNow(), result.NormalizedAt);
    }

    [Fact]
    public void Normalize_rejects_a_non_positive_quantity()
    {
        var received = new ReceivedOrder("ORD-77", [new ReceivedLine("SKU-1", 0, 10m)], null);

        Assert.Throws<FormatException>(() => OrderNormalizer.Normalize(received, Clock));
    }
}
```

## EF tests: SQLite in-memory, connection held open

```csharp
public sealed class OrdersContextFixture : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    public OrdersDbContext DbContext { get; private set; } = null!;
    public OrdersContext Context { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // The in-memory database only exists while a connection to it is open, so keep one open
        // for the lifetime of the fixture.
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlite(_connection)
            .Options;

        DbContext = new OrdersDbContext(options);
        await DbContext.Database.EnsureCreatedAsync();
        Context = new OrdersContext(new TestDbContextFactory<OrdersDbContext>(options));
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
```

**Never use the EF `InMemory` provider.** It is not a relational engine: it ignores transactions,
uniqueness, raw SQL, column types, and query translation, so a test that passes against it can fail
against SQL Server. SQLite in-memory is a real SQL engine, cheap enough to run per test.

### What SQLite does NOT catch

SQLite will not reproduce, and you must verify these against a real SQL Server before shipping:

- `EFCore.BulkExtensions` behaviour, including the staging table that bulk writes create without
  `UseTempDB = true`, and the requirement of an explicit transaction when it is set;
- the execution-strategy interaction that makes a bare `BeginTransactionAsync()` throw once
  `EnableRetryOnFailure` is configured;
- `datetimeoffset` precision and collation;
- `MERGE`-shaped upserts and locking behaviour;
- any provider-specific SQL hint, `SqlHints`, or raw `SqlQuery`.

Write those tests so they can run against both: keep the SQL dialect behind a provider switch or mark
them as an explicit integration test category you run before a release.

## Endpoint, auth and rate limiting tests

```csharp
public sealed class OrderEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public OrderEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Ingest_without_a_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/orders/ingest", new { reference = "ORD-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_with_an_invalid_body_returns_400_problem_details()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/orders/ingest", new { reference = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem!.Errors);
    }

    public void Dispose() => _factory.Dispose();
}
```

`Program` must be reachable for `WebApplicationFactory<Program>`: with top-level statements add
`public partial class Program;` at the end of `Program.cs`, or the type is internal to the generated
entry-point class.

Also worth covering with `WebApplicationFactory`: the `403` path for a wrong client, the `429` path
with `Retry-After`, `/health` returning 200 anonymously, and `/openapi/v1.json` being 404 when
`OpenApi:Enabled` is false.

## Quality gates in the build

```xml
<!-- Directory.Build.props — applies to src and tests -->
<PropertyGroup>
  <LangVersion>14</LangVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>

  <!-- IDE0005 (unused using) is only reported during build when the XML documentation
       file is generated; CS1591 (public member without XML doc) is silenced because
       requiring XML docs on every public member is not a rule of this standard. -->
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);CS1591</NoWarn>
</PropertyGroup>

<ItemGroup>
  <!-- Static analysis in every project. `0.0.0-PLACEHOLDER` is a placeholder, not a version:
       replace it with the current stable release (check NuGet) before the first restore. It IS
       well-formed semver - 0.0.0 with the pre-release tag PLACEHOLDER - but no such version is
       published, so restore fails with NU1102 if you forget. That is the point: a loud failure
       beats a silently unpinned analyzer. It also keeps this file valid XML - a literal such as
       Version="<latest-stable>" would put a raw `<` inside an attribute value. Bump it here only;
       the other projects Update it. -->
  <PackageReference Include="SonarAnalyzer.CSharp" Version="0.0.0-PLACEHOLDER">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
</ItemGroup>
```

The `GenerateDocumentationFile` + `NoWarn CS1591` combination is not a trick: without the documentation
file, IDE0005 is only an IDE squiggle, and unused usings accumulate until the diff stops being
readable. Cleanup:

```bash
dotnet format style --diagnostics IDE0005
```

Pin the analyzer version centrally here and let each project `Update` it, so there is one place to
bump it. There is no separate lint command: the analyzer runs on every `dotnet build`.

**Zero warnings is the bar.** A build with warnings trains everyone to ignore the output, and then the
one warning that mattered is one of the forty.

## Test project layout

Mirror the layers, one project per layer:

```
tests/Contoso.Orders.Domain.Tests/
tests/Contoso.Orders.Infrastructure.Persistence.Tests/
tests/Contoso.Orders.Application.UseCases.Tests/
tests/Contoso.Orders.Api.Tests/
```

Fakes live next to the tests of the layer that uses them; a `Fake*` shared across layers belongs in the
lowest layer that needs it, not in a fourth "Common.Tests" project.

## Anti-patterns

- The EF `InMemory` provider for anything but a trivial sanity check.
- SQLite in-memory where the connection is not held open — "table not found" on the second test.
- Declaring the bulk/transaction/precision behaviour covered when it was only tested on SQLite.
- `coverlet.collector` + `--collect` on a platform-runner project (silently produces no coverage).
- A mocking library for your own ports.
- Tests that only exercise the endpoints; no unit test for the pure logic, which is where the value is.
- Warnings tolerated because "they were there before".
- `TreatWarningsAsErrors` disabled globally to make one analyzer quiet.
- A single test project for the whole solution, so layer coverage is invisible.
- `DateTime.Now` in a class under test, making every time assertion flaky.
- Testing private methods instead of the public contract of the pure class.
