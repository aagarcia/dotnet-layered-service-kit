// Punto de entrada de la capa de presentación. El orden de abajo no es cosmético: cada paso
// depende de que el anterior ya esté configurado.
//
// `Contoso.Orders` es el namespace de ejemplo - reemplázalo por `<Company>.<Product>` antes de compilar.
// Las sentencias de nivel superior viven en el namespace global, así que todo lo que el punto de
// entrada toque por nombre necesita su namespace importado aquí - incluidos los tipos declarados en
// el propio namespace Api de este proyecto.
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

// 1. Telemetría primero, para que todo lo que se registre después quede instrumentado.
builder.Services.AddObservability(builder.Configuration);

// 2. Capas. Persistencia antes que Application: la factory del DbContext y los servicios de
//    auditoría se inyectan en los handlers.
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddApplicationServices();

// 3. Puertos cuyas implementaciones viven en esta capa.
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton<IQueueService, QueueService>();

// 4. Autenticación. Authority + Audience es toda la configuración: el descubrimiento OIDC, la
//    rotación de claves y el desfase de reloj los maneja el framework. Nunca escribas un validador
//    de JWT.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];
        options.Audience = jwt["Audience"];
    });

// Deny por defecto: cada ruta exige un token válido salvo que se excluya con .AllowAnonymous().
// Una policy personalizada es solo para lo que el framework no puede expresar: aquí, "emitido para
// este cliente", que es lo que conserva la distinción entre 401 (no autenticado) y 403 (llamador
// incorrecto).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(ClientPolicy.Name, policy =>
        policy.Requirements.Add(new RequiredClientIdRequirement(jwt["RequiredClientId"])));
builder.Services.AddSingleton<IAuthorizationHandler, RequiredClientIdHandler>();

// 5. Rate limiting. Por instancia, no distribuido: dilo en docs/ARCHITECTURE.md.
//    RateLimitSettings es NUESTRA clase de options, deliberadamente NO llamada RateLimiterOptions:
//    ese nombre es de Microsoft.AspNetCore.RateLimiting.RateLimiterOptions (pitfall 12).
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

// 6. ProblemDetails y el manejador de excepciones global.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks();

// 7. OpenAPI + Scalar solo donde estén habilitados. Apagados por defecto, para que producción no
//    sea el primer lugar donde se publica la superficie de la API.
var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", false);
if (openApiEnabled)
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

// 8. Pipeline, de fuera hacia adentro. Exactamente UNA llamada a UseRateLimiter(): cada llamada
//    agrega otra instancia de RateLimitingMiddleware, y cada instancia vuelve a aplicar el
//    limitador global *y* la policy del endpoint, así que dos llamadas cobran dos veces el
//    presupuesto global y corren la policy por claims antes de la autenticación. La policy de este
//    template particiona GLOBALMENTE, así que la única llamada va aquí, antes de la autenticación.
//    Si cambias a una partición por cliente (azp/appid), MUEVE esta llamada después de
//    UseAuthentication(); para tener ambos presupuestos, configura un GlobalLimiter por IP más una
//    policy de endpoint por cliente en los mismos options de AddRateLimiter. Tabla completa: 07.
app.UseExceptionHandler();
app.UseRateLimiter();

if (openApiEnabled)
{
    app.MapOpenApi();                     // /openapi/v1.json
    app.MapScalarApiReference();          // /scalar/v1
}

app.UseAuthentication();
app.UseAuthorization();

// 9. Endpoints: un MapGroup + Map<Module>Endpoints() por módulo.
//    No AddCors/UseCors: esta es una API servidor a servidor. No AddControllers: solo minimal APIs.
app.MapGroup("/orders").MapOrderEndpoints();
app.MapHealthEndpoints();

await app.RunAsync();

// Alcanzable por WebApplicationFactory<Program> desde el proyecto de tests.
// S1118 ("la clase utilitaria debería ser static o tener un constructor protegido") es un falso
// positivo en este marcador: el compilador genera la clase para las sentencias de nivel superior de
// arriba, y no puede volverse static sin romper WebApplicationFactory<Program>. Suprimido a
// propósito.
#pragma warning disable S1118
public partial class Program;
#pragma warning restore S1118
