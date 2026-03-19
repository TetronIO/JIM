using Microsoft.Extensions.DependencyInjection;

namespace JIM.Application;

/// <summary>
/// Creates short-lived <see cref="JimApplication"/> instances via the DI container.
/// Each call to <see cref="Create"/> resolves a new transient JimApplication with its own
/// DbContext, preventing concurrent access errors when multiple tasks run in parallel.
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
