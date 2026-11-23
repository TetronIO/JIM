// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;
using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Security;

namespace JIM.Web.Models;

/// <summary>
/// Whether a change to a Trusted Certificate would stop everyone signing in, so the portal warns first (#2363). A
/// warning with typed confirmation rather than a block: swapping one certificate authority for another is routine.
/// </summary>
public static class SignInLockoutGuard
{
    /// <param name="status">The identity provider's status as last checked.</param>
    /// <param name="certificateId">The certificate being changed.</param>
    /// <param name="deleting">Whether it is being deleted.</param>
    /// <param name="enabledAfter">Whether it will be enabled after the change, where the change says.</param>
    /// <param name="trustedForAfter">What it will be trusted for after the change, where the change says.</param>
    public static bool WouldStopSignIn(IdentityProviderTrustStatus status, Guid certificateId, bool deleting = false,
        bool? enabledAfter = null, CertificateTrustPurpose? trustedForAfter = null)
    {
        if (!status.SignInDependsOn(certificateId))
            return false;

        return deleting
            || enabledAfter == false
            || (trustedForAfter.HasValue && !trustedForAfter.Value.HasFlag(CertificateTrustPurpose.SignIn));
    }

    /// <summary>
    /// What the confirmation says.
    /// </summary>
    public static string Warning(string certificateName, string? host) => host == null
        ? string.Format(CultureInfo.CurrentCulture, SignInText.LockoutWarning, certificateName)
        : string.Format(CultureInfo.CurrentCulture, SignInText.LockoutWarningForHost, certificateName, host);
}
