// Un archivo por módulo. Agregar un módulo significa agregar este archivo y una línea en
// Program.cs - no editar un switch que crece en algún lado.
//
// Ver references/06-api-presentation.md.
using Contoso.Orders.Api.Filters;
using Contoso.Orders.Api.Commons;
using Contoso.Orders.Api.RateLimiting;
using Contoso.Orders.Api.Security;
using Contoso.Orders.Application.UseCases.Commons.DTOs;
using Contoso.Orders.Application.UseCases.IFeatures;

namespace Contoso.Orders.Api.Endpoints;

public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder builder)
    {
        // Ingesta: validar -> normalizar -> encolar -> 202 Accepted.
        builder.MapPost("/ingest", IngestAsync)
            .AddEndpointFilter<ValidationFilter<ReceivedOrder>>()
            .RequireAuthorization(ClientPolicy.Name)
            .RequireRateLimiting(OrderIngestRateLimiterPolicy.PolicyName)
            .WithSummary("Receives an order, normalizes it and enqueues it.")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Consulta de solo lectura contra un SDK de terceros.
        builder.MapGet("/shipments/{referenceCode}", GetShipmentStatusAsync)
            .AddEndpointFilter<IdValidationFilter>()
            .RequireRateLimiting(CarrierQueryRateLimiterPolicy.PolicyName)
            .WithSummary("Returns the shipment status from the carrier.");

        return builder;
    }

    // Los handlers devuelven un DTO de resultado; este helper es el único lugar que lo mapea a
    // HTTP. Nunca IActionResult: le oculta el contrato a OpenAPI y al compilador.
    private static async Task<IResult> IngestAsync(
        ReceivedOrder request, IOrderHandler handler, CancellationToken cancellationToken) =>
        HandlingHelper.ToHttpResult(await handler.HandleAsync(request, cancellationToken));

    private static async Task<IResult> GetShipmentStatusAsync(
        string referenceCode, IShipmentStatusHandler handler, CancellationToken cancellationToken) =>
        HandlingHelper.ToHttpResult(await handler.HandleAsync(referenceCode, cancellationToken));
}
