// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;

namespace JIM.Web.Authentication;

/// <summary>
/// The identity provider's last known trust status (#2363).
/// </summary>
public interface IIdentityProviderTrustMonitor
{
    /// <summary>
    /// The status as last checked, without checking.
    /// </summary>
    IdentityProviderTrustStatus Current { get; }

    /// <summary>
    /// The status, checked again unless it was checked within the last few seconds. Safe to call from anonymous
    /// requests.
    /// </summary>
    Task<IdentityProviderTrustStatus> GetFreshStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks now, whenever it was last checked. For an administrator's action (trusting a certificate), never for an
    /// anonymous request.
    /// </summary>
    Task<IdentityProviderTrustStatus> RecheckAsync(CancellationToken cancellationToken);
}
