// El único punto de registro de la capa de aplicación. Los puertos hacia infraestructura se
// declaran aquí (IServices) y se implementan en la capa Api. Ver references/05-application-usecases.md.
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
        // Los handlers son Scoped: dependen del Context de un módulo, que es una unidad de trabajo.
        services.AddScoped<IOrderHandler, OrderHandler>();

        // Los validadores se registran por ensamblado. Una lista escrita a mano es una lista que se
        // omite en silencio el validador agregado la semana pasada.
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

        services.AddSingleton(TimeProvider.System);

        // La config de Mapster es estática, así que no puede resolver un servicio inyectado. Todo lo
        // que dependa del tiempo o de las options se aplica en el handler después del mapping.
        MapsterGlobalConfig.Initialize();
        services.AddMapster();

        // Los mensajes de validación nombran el campo JSON, no la propiedad de C#.
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
