// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace JIM.Web.Shared;

/// <summary>
/// An <see cref="ErrorBoundary"/> that logs unhandled rendering exceptions.
/// </summary>
public sealed class LoggingErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<LoggingErrorBoundary> Logger { get; set; } = null!;

    protected override Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "Unhandled exception caught by error boundary");
        return Task.CompletedTask;
    }
}
