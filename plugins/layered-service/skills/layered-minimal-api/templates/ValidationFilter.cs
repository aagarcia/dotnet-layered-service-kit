// Generic endpoint filter that validates the request body with the IValidator<T> resolved from DI,
// before the handler runs. Produces a ProblemDetails body with an `errors` dictionary - the same
// shape the framework uses, so clients need one error model.
//
// See references/06-api-presentation.md.
using FluentValidation;

namespace Contoso.Orders.Api.Filters;

/// <summary>Validates the body (type T) before the endpoint handler executes.</summary>
public sealed class ValidationFilter<T> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var target = context.Arguments.OfType<T>().FirstOrDefault();
        if (target is null)
        {
            return Results.BadRequest();
        }

        var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null)
        {
            // No validator registered for this DTO: not a mistake, just an unvalidated endpoint.
            return await next(context);
        }

        var result = await validator.ValidateAsync(target);
        return result.IsValid ? await next(context) : Results.ValidationProblem(result.ToDictionary());
    }
}
