// Filtro genérico de endpoint que valida el body del request con el IValidator<T> resuelto desde DI,
// antes de que corra el handler. Produce un body ProblemDetails con un diccionario `errors` - la
// misma forma que usa el framework, así que los clientes necesitan un solo modelo de error.
//
// Ver references/06-api-presentation.md.
using FluentValidation;

namespace Contoso.Orders.Api.Filters;

/// <summary>Valida el body (tipo T) antes de que se ejecute el handler del endpoint.</summary>
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
            // Sin validador registrado para este DTO: no es un error, es un endpoint sin validar.
            return await next(context);
        }

        var result = await validator.ValidateAsync(target);
        return result.IsValid ? await next(context) : Results.ValidationProblem(result.ToDictionary());
    }
}
