// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace JIM.Application;

/// <summary>
/// Creates short-lived <see cref="JimApplication"/> instances via the DI container.
/// Each call to <see cref="Create"/> resolves a new transient JimApplication with its own
/// DbContext, preventing concurrent access errors when multiple tasks run in parallel.
/// </summary>
/// <remarks>
/// Each instance is resolved from a DI scope of its own, which it disposes when it is disposed. The factory is a
/// singleton, so resolving from the provider it was constructed with would resolve from the root scope, and the root
/// scope keeps every disposable transient it creates until the process exits, whether or not the caller disposed it.
/// That retained every <see cref="JimApplication"/>, repository and DbContext the factory ever created (#2115).
/// </remarks>
public class JimApplicationFactory : IJimApplicationFactory
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public JimApplicationFactory(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public JimApplication Create()
    {
        var scope = _serviceScopeFactory.CreateScope();
        var owned = false;
        try
        {
            var jim = scope.ServiceProvider.GetRequiredService<JimApplication>();
            jim.OwnScope(scope);
            owned = true;
            return jim;
        }
        finally
        {
            // Resolution failed (the database is unreachable, say): nothing will ever dispose this scope otherwise.
            if (!owned)
                scope.Dispose();
        }
    }
}
