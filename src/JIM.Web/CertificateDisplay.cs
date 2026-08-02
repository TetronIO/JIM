// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;

namespace JIM.Web;

/// <summary>
/// How certificate values are written wherever the portal shows them, so the certificate card, the trust dialog and
/// anything that follows present the same value the same way.
/// </summary>
public static class CertificateDisplay
{
    /// <summary>
    /// Groups a thumbprint into pairs, the way certificate viewers present one, so it can be compared by eye against
    /// the value the server's owner reads out.
    /// </summary>
    public static string FormatThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return "unknown";

        return string.Join(' ', Enumerable
            .Range(0, thumbprint.Length / 2)
            .Select(i => thumbprint.Substring(i * 2, 2)));
    }

    /// <summary>
    /// Distinguished names read as "CN=dc01.corp.local, O=Corp"; the common name alone is what an administrator
    /// recognises, so lead with it and drop the rest.
    /// </summary>
    public static string FormatDistinguishedName(string? distinguishedName)
    {
        return PresentedServerCertificate.CommonNameOf(distinguishedName);
    }
}
