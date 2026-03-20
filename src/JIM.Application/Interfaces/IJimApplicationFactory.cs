namespace JIM.Application.Interfaces;

/// <summary>
/// Factory for creating short-lived <see cref="JimApplication"/> instances.
/// In Blazor Server, each async operation should use its own JimApplication
/// to avoid DbContext concurrency issues across overlapping lifecycle methods.
/// </summary>
public interface IJimApplicationFactory
{
    /// <summary>
    /// Creates a new <see cref="JimApplication"/> instance with its own DbContext.
    /// The caller is responsible for disposing the returned instance.
    /// </summary>
    JimApplication Create();
}
