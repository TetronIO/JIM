// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors;

/// <summary>
/// Turns a failed connection into the certificate that caused it, for any connector that speaks TLS.
/// </summary>
/// <remarks>
/// Every TLS connector needs the same six steps: load JIM's trusted certificates, look at what the server presents,
/// leave the original failure alone when the certificate turns out to be fine or the server cannot be reached, log
/// what was found, build the exception that carries the certificate to the administrator, and dispose. Only two steps
/// are genuinely per-connector, and they stay with the connector: recognising whether a given failure is a TLS trust
/// failure at all (the LDAP connector knows structurally, from the catch site; an HTTP-based connector has to walk the
/// inner-exception chain), and any connector-specific opt-out from certificate validation.
/// <para>
/// This is only shareable because a connector can now say where it connects, through
/// <see cref="IConnectorSecureEndpoint"/>: the endpoint carries the host, the port, the timeout, and what to call the
/// far end, so even the wording of the failure stops being per-connector.
/// </para>
/// <para>
/// Nothing here trusts anything or makes a connection succeed. The handshake it opens is refused by design, and
/// exists only so an administrator is told "this certificate, this problem" rather than "the server is unavailable".
/// </para>
/// </remarks>
public static class ServerCertificateDiagnosis
{
    /// <summary>
    /// Describes the certificate behind a failed connection, when that is what the failure was.
    /// </summary>
    /// <param name="connector">The connector, which resolves the endpoint from its own settings. No caller ever names a host and port.</param>
    /// <param name="settingValues">The Connected System's setting values.</param>
    /// <param name="certificateProvider">JIM's certificate store, supplied as additional trust anchors so a certificate the store already vouches for is not misreported as untrusted. Null where the connector was never given one.</param>
    /// <param name="originalException">The failure being explained, kept as the inner exception.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    /// <param name="probe">Overrides how the server is looked at. For tests; production callers omit it.</param>
    /// <returns>
    /// The rejection to report, or null when there is nothing to report: the system is not configured for an
    /// encrypted connection, the server could not be reached, or its certificate turns out to be fine and the
    /// original failure was about something else.
    /// </returns>
    /// <remarks>
    /// Returned rather than thrown, because the callers want it both ways. Setting validation needs it as a value to
    /// put on a failed validation result, which is what the portal reads to render the certificate; a connection
    /// being opened during a run throws it, so it reaches the Activity. A helper that threw would force the
    /// validation path into a catch block, which is exactly how a connector ends up reporting a certificate failure as
    /// an unhandled error.
    /// </remarks>
    public static ServerCertificateRejectedException? Describe(
        IConnectorSecureEndpoint connector,
        List<ConnectedSystemSettingValue> settingValues,
        ICertificateProvider? certificateProvider,
        Exception? originalException,
        ILogger logger,
        Func<SecureEndpoint, IReadOnlyCollection<X509Certificate2>, ServerCertificateDiagnostic?>? probe = null)
    {
        // Not configured for an encrypted connection, so whatever went wrong was not about a certificate.
        if (connector.ResolveSecureEndpoint(settingValues) is not { } endpoint)
            return null;

        var trustedCertificates = LoadTrustedCertificates(certificateProvider);

        try
        {
            var diagnostic = probe != null
                ? probe(endpoint, trustedCertificates)
                : ServerCertificateProbe.Probe(endpoint.Host, endpoint.Port, trustedCertificates, endpoint.Timeout,
                    logger, endpoint.ServerDescription, endpoint.SecureTransportName);

            // Nothing wrong with the certificate, or the server could not be reached to look at one. Either way the
            // original failure stands: blaming the certificate would send an administrator after a problem that is
            // not there.
            if (diagnostic == null || diagnostic.FailureReason == ServerCertificateFailureReason.None)
                return null;

            logger.Error("The {Transport} connection to {Host}:{Port} was refused because of the {ServerDescription}'s certificate. Reason: {Reason}. Subject: {Subject}, Issuer: {Issuer}, Thumbprint: {Thumbprint}, Valid to: {ValidTo}",
                endpoint.SecureTransportName, LogSanitiser.Sanitise(endpoint.Host), endpoint.Port, endpoint.ServerDescription,
                // codeql[cs/cleartext-storage-of-sensitive-information] FailureReason is an enum (UntrustedIssuer et al.), not a credential
                diagnostic.FailureReason, LogSanitiser.Sanitise(diagnostic.Subject), LogSanitiser.Sanitise(diagnostic.Issuer),
                LogSanitiser.Sanitise(diagnostic.Thumbprint), diagnostic.ValidTo);

            return new ServerCertificateRejectedException(
                $"The {endpoint.ServerDescription}'s certificate was rejected: {diagnostic.Remediation}",
                diagnostic,
                originalException);
        }
        finally
        {
            foreach (var certificate in trustedCertificates)
                certificate.Dispose();
        }
    }

    /// <summary>
    /// The enabled certificates from JIM's own store, as the connector interfaces need them: synchronously.
    /// </summary>
    /// <remarks>
    /// The connector contract is synchronous but the store is read asynchronously, so the wait has to happen
    /// somewhere. <see cref="Task.Run(Func{Task})"/> keeps it off the caller's synchronisation context: setting
    /// validation is invoked from Blazor Server circuits, which have one, and blocking on it directly deadlocks the
    /// circuit rather than timing out. The caller owns disposing what comes back.
    /// </remarks>
    public static List<X509Certificate2> LoadTrustedCertificates(ICertificateProvider? certificateProvider)
    {
        if (certificateProvider == null)
            return [];

        var provider = certificateProvider;
        return Task.Run(provider.GetTrustedCertificatesAsync).GetAwaiter().GetResult();
    }
}
