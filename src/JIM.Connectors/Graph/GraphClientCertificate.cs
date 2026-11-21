// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors.Graph;

/// <summary>
/// Loads and checks the certificate an app registration authenticates with: a Base64-encoded PFX holding the
/// certificate and its RSA private key. Used when the settings are saved, so an unusable certificate is refused there
/// with a reason, and again when a run authenticates.
/// </summary>
/// <remarks>
/// The PFX is held on the Connected System as an encrypted setting rather than as a file path, so the private key is
/// encrypted at rest with every other credential and needs no volume mounted into both the web and worker containers.
/// The key is loaded into an ephemeral key set, so it is never written to a key store on disk.
/// </remarks>
internal static class GraphClientCertificate
{
    public static GraphClientCertificateLoadResult Load(string base64Pfx, string? password, DateTime utcNow)
    {
        byte[] pfx;
        try
        {
            pfx = Convert.FromBase64String(base64Pfx.Trim());
        }
        catch (FormatException)
        {
            return GraphClientCertificateLoadResult.Failed(
                $"The {GraphConnectorConstants.SettingCertificate} is not valid Base64. Supply the PFX file's contents Base64-encoded, " +
                "for example with [Convert]::ToBase64String([IO.File]::ReadAllBytes('app.pfx')) in PowerShell.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            // The loader reports a wrong password and a document that is not a PFX alike, so the message names both.
            return GraphClientCertificateLoadResult.Failed(
                $"The {GraphConnectorConstants.SettingCertificate} could not be opened as a PFX (PKCS #12) file with the " +
                $"{GraphConnectorConstants.SettingCertificatePassword} given. Check that it is the Base64 encoding of a PFX " +
                "file and that the password is correct.");
        }

        var error = Check(certificate, utcNow);
        if (error != null)
        {
            certificate.Dispose();
            return GraphClientCertificateLoadResult.Failed(error);
        }

        return GraphClientCertificateLoadResult.Loaded(certificate, ExpiryWarning(certificate, utcNow));
    }

    private static string? Check(X509Certificate2 certificate, DateTime utcNow)
    {
        if (!certificate.HasPrivateKey)
            return $"The {GraphConnectorConstants.SettingCertificate} contains no private key. Export the certificate from its store as a PFX with its private key included.";

        using var rsa = certificate.GetRSAPublicKey();
        if (rsa == null)
            return $"The {GraphConnectorConstants.SettingCertificate} does not have an RSA key. Microsoft Entra ID signs client assertions with RSA, so the certificate needs an RSA key of at least {GraphConnectorConstants.MinimumRsaKeySizeBits} bits.";

        if (rsa.KeySize < GraphConnectorConstants.MinimumRsaKeySizeBits)
            return $"The {GraphConnectorConstants.SettingCertificate} has a {rsa.KeySize}-bit RSA key. Microsoft Entra ID needs at least {GraphConnectorConstants.MinimumRsaKeySizeBits} bits.";

        var notBefore = certificate.NotBefore.ToUniversalTime();
        if (notBefore > utcNow)
            return $"The {GraphConnectorConstants.SettingCertificate} is not valid until {Format(notBefore)}.";

        var notAfter = certificate.NotAfter.ToUniversalTime();
        if (notAfter <= utcNow)
            return $"The {GraphConnectorConstants.SettingCertificate} expired on {Format(notAfter)}. Upload a renewed certificate to the app registration and supply it here.";

        return null;
    }

    private static string? ExpiryWarning(X509Certificate2 certificate, DateTime utcNow)
    {
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var remaining = notAfter - utcNow;
        if (remaining > TimeSpan.FromDays(GraphConnectorConstants.CertificateExpiryWarningDays))
            return null;

        var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
        return $"The {GraphConnectorConstants.SettingCertificate} expires on {Format(notAfter)}, in {days} day{(days == 1 ? "" : "s")}. " +
               "Imports and exports stop when it does, so renew it and supply the new certificate here before then.";
    }

    private static string Format(DateTime utc) => utc.ToString("d MMMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
