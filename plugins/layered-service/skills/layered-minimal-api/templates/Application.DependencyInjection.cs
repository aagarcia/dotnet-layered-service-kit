// The single registration point of the application layer. Ports to infrastructure are declared
// here (IServices) and implemented in the Api layer. See references/05-application-usecases.md.
using System.Reflection;
using System.Text.Json.Serialization;
using Contoso.Orders.Application.UseCases.Commons.Mappings;
using Contoso.Orders.Application.UseCases.Features;
using Contoso.Orders.Application.UseCases.IFeatures;
using FluentValidation;
using Mapster;
using Microsoft.Extensions.DependencyInjection;

namespace Contoso.Orders.Application.UseCases;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Handlers are Scoped: they depend on a module Context, which is a unit of work.
        services.AddScoped<IOrderHandler, OrderHandler>();

        // Validators register by assembly. A hand-written list is a list that silently misses the
        // validator added last week.
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

        services.AddSingleton(TimeProvider.System);

        // Mapster config is static, so it cannot resolve an injected service. Anything
        // time-dependent or option-dependent is applied in the handler after the mapping.
        MapsterGlobalConfig.Initialize();
        services.AddMapster();

        // Validation messages name the JSON field, not the C# property.
        ValidatorOptions.Global.DisplayNameResolver = (_, member, _) =>
        {
            if (member is null)
            {
                return null;
            }

            var attribute = member.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                .FirstOrDefault() as JsonPropertyNameAttribute;
            return attribute?.Name ?? member.Name;
        };

        return services;
    }
}
