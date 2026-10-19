// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Services;

/// <summary>
/// Tells components in one user's circuit that the JIM certificate store changed somewhere other than where they are
/// looking: trusting a certificate from the identity provider banner, which lives in the layout, while the Trusted
/// Certificates page is open beneath it (#2363). Scoped, so it never crosses between users.
/// </summary>
public sealed class TrustedCertificateChanges
{
    /// <summary>
    /// Raised after a certificate is added or changed.
    /// </summary>
    public event Func<Task>? Changed;

    public async Task NotifyAsync()
    {
        var handlers = Changed;
        if (handlers == null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
