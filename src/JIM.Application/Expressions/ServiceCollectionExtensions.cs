using Microsoft.Extensions.DependencyInjection;

namespace JIM.Application.Expressions;

/// <summary>
/// Extension methods for registering expression evaluation services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds expression evaluation services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddExpressionEvaluation(this IServiceCollection services)
    {
        services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
        return services;
    }
}
