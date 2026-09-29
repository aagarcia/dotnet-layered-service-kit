// One file per module. Adding a module means adding this file and one line in Program.cs -
// not editing a growing switch somewhere.
//
// See references/06-api-presentation.md.
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
        // Ingestion: validate -> normalize -> enqueue -> 202 Accepted.
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

        // Read-only query against a third-party SDK.
        builder.MapGet("/shipments/{referenceCode}", GetShipmentStatusAsync)
            .AddEndpointFilter<IdValidationFilter>()
            .RequireRateLimiting(CarrierQueryRateLimiterPolicy.PolicyName)
            .WithSummary("Returns the shipment status from the carrier.");

        return builder;
    }

    // Handlers return a result DTO; this helper is the single place that maps it to HTTP.
    // Never IActionResult: it hides the contract from OpenAPI and from the compiler.
    private static async Task<IResult> IngestAsync(
        ReceivedOrder request, IOrderHandler handler, CancellationToken cancellationToken) =>
        HandlingHelper.ToHttpResult(await handler.HandleAsync(request, cancellationToken));

    private static async Task<IResult> GetShipmentStatusAsync(
        string referenceCode, IShipmentStatusHandler handler, CancellationToken cancellationToken) =>
        HandlingHelper.ToHttpResult(await handler.HandleAsync(referenceCode, cancellationToken));
}
