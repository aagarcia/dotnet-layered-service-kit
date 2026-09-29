# 12 — Pruebas y calidad

## La regla

xUnit v3 sobre Microsoft.Testing.Platform. Fakes escritos a mano para los puertos. Pruebas de EF sobre
**SQLite en memoria**, nunca el provider `InMemory` de EF. Cobertura mediante
`Microsoft.Testing.Extensions.CodeCoverage`. Analizador configurado en `Directory.Build.props`. **Compilar
con cero advertencias.**

## Runner de pruebas

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

Con el runner de la plataforma, `coverlet.collector` + `--collect` **no produce nada** — es una extensión
de VSTest y VSTest no está corriendo:

```bash
dotnet test --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
```

Eso funciona gracias a `Microsoft.Testing.Extensions.CodeCoverage`. Una referencia vestigial a
`coverlet.collector` en un proyecto de pruebas es peso muerto: quítala en lugar de dejarla ahí para
engañar a la siguiente persona.

## Fakes, no mocks, para los puertos

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

Por qué fakes escritos a mano: son el contrato público del puerto hecho ejecutable, sobreviven a un
refactor del sitio de llamada y permiten que una prueba afirme sobre *qué* se encoló en lugar de sobre
qué métodos se llamaron. Usa una librería de mocks solo cuando el colaborador no es tuyo.

También vale la pena tener: un `FixedTimeProvider` para todo lo que estampa una marca de tiempo.

## Lógica pura: pruebas unitarias simples

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

## Pruebas de EF: SQLite en memoria, conexión mantenida abierta

```csharp
public sealed class OrdersContextFixture : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    public OrdersDbContext DbContext { get; private set; } = null!;
    public OrdersContext Context { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // La base de datos en memoria solo existe mientras hay una conexión abierta, así que mantén
        // una abierta durante toda la vida del fixture.
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

**Nunca uses el provider `InMemory` de EF.** No es un motor relacional: ignora las transacciones, la
unicidad, el SQL crudo, los tipos de columna y la traducción de consultas, así que una prueba que pasa
contra él puede fallar contra SQL Server. SQLite en memoria es un motor SQL real, lo bastante barato para
ejecutarse en cada prueba.

### Lo que SQLite NO detecta

SQLite no va a reproducir esto, y debes verificarlo contra un SQL Server real antes de publicar:

- El comportamiento de `EFCore.BulkExtensions`, incluida la tabla de staging que crean las escrituras
  masivas sin `UseTempDB = true`, y el requisito de una transacción explícita cuando sí está activo;
- La interacción con la estrategia de ejecución que hace que un `BeginTransactionAsync()` a secas lance
  una excepción en cuanto `EnableRetryOnFailure` está configurado;
- La precisión y la intercalación de `datetimeoffset`;
- Los upserts con forma de `MERGE` y el comportamiento de bloqueo;
- Cualquier pista SQL específica del proveedor, `SqlHints` o `SqlQuery` crudo.

Escribe esas pruebas de modo que puedan correr contra ambos: deja el dialecto SQL detrás de un interruptor
de provider o márcalas como una categoría explícita de pruebas de integración que ejecutas antes de una
publicación.

## Pruebas de endpoint, autenticación y rate limiting

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

`Program` debe ser accesible para `WebApplicationFactory<Program>`: con sentencias de nivel superior añade
`public partial class Program;` al final de `Program.cs`, o el tipo queda interno de la clase de punto de
entrada generada.

También vale la pena cubrir con `WebApplicationFactory`: la ruta `403` para un cliente incorrecto, la ruta
`429` con `Retry-After`, `/health` devolviendo 200 de forma anónima, y `/openapi/v1.json` devolviendo 404
cuando `OpenApi:Enabled` es false.

## Barreras de calidad en la compilación

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
  <!-- Análisis estático en todos los proyectos. `0.0.0-PLACEHOLDER` es un marcador de posición,
       no una versión: reemplázalo por la release estable actual (mira NuGet) antes del primer
       restore. SÍ es semver bien formado - 0.0.0 con el tag prerelease PLACEHOLDER - pero esa
       versión no está publicada, así que el restore falla con NU1102 si te olvidas. De eso se
       trata: un fallo visible es mejor que un analizador sin versión fijada en silencio. También
       mantiene este archivo como XML válido - un literal como Version="<latest-stable>" pondría un
       `<` crudo dentro del valor de un atributo. Se sube aquí únicamente; los demás proyectos lo
       actualizan con Update. -->
  <PackageReference Include="SonarAnalyzer.CSharp" Version="0.0.0-PLACEHOLDER">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
</ItemGroup>
```

La combinación de `GenerateDocumentationFile` + `NoWarn CS1591` no es un truco: sin el archivo de
documentación, IDE0005 es solo un subrayado del IDE, y las directivas using sin usar se acumulan hasta que
el diff deja de ser legible. Limpieza:

```bash
dotnet format style --diagnostics IDE0005
```

Fija la versión del analizador centralmente aquí y deja que cada proyecto la haga `Update`, así hay un
único sitio donde incrementarla. No hay un comando de lint aparte: el analizador corre en cada
`dotnet build`.

**Cero advertencias es el listón.** Una compilación con advertencias entrena a todos a ignorar la salida, y
entonces la única advertencia que importaba es una de las cuarenta.

## Estructura de los proyectos de pruebas

Refleja las capas, un proyecto por capa:

```
tests/Contoso.Orders.Domain.Tests/
tests/Contoso.Orders.Infrastructure.Persistence.Tests/
tests/Contoso.Orders.Application.UseCases.Tests/
tests/Contoso.Orders.Api.Tests/
```

Los fakes viven junto a las pruebas de la capa que los usa; un `Fake*` compartido entre capas pertenece a la
capa más baja que lo necesita, no a un cuarto proyecto «Common.Tests».

## Anti-patrones

- El provider `InMemory` de EF para algo que no sea una comprobación de cordura trivial.
- SQLite en memoria donde la conexión no se mantiene abierta — «table not found» en la segunda prueba.
- Declarar cubierto el comportamiento de lotes/transacción/precisión cuando solo se probó en SQLite.
- `coverlet.collector` + `--collect` en un proyecto con runner de plataforma (produce cobertura en
  silencio: ninguna).
- Una librería de mocks para tus propios puertos.
- Pruebas que solo ejercitan los endpoints; ninguna prueba unitaria de la lógica pura, que es donde está
  el valor.
- Advertencias toleradas porque «ya estaban ahí».
- `TreatWarningsAsErrors` desactivado globalmente para silenciar un analizador.
- Un único proyecto de pruebas para toda la solución, así la cobertura por capa es invisible.
- `DateTime.Now` en una clase bajo prueba, haciendo que toda aserción de tiempo sea inestable.
- Probar métodos privados en lugar del contrato público de la clase pura.
