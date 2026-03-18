using JIM.Application;
using Microsoft.Extensions.DependencyInjection;

namespace JIM.Web.Services;

/// <summary>
/// Creates short-lived <see cref="JimApplication"/> instances for Blazor Server components.
/// Each call to <see cref="Create"/> resolves a new transient JimApplication with its own
/// DbContext, preventing concurrent access errors when multiple Blazor lifecycle methods
/// overlap (e.g. OnInitializedAsync and OnAfterRenderAsync).
/// </summary>
public class JimApplicationFactory : IJimApplicationFactory
{
    private readonly IServiceProvider _serviceProvider;

    public JimApplicationFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public JimApplication Create()
    {
        return _serviceProvider.GetRequiredService<JimApplication>();
    }
}
