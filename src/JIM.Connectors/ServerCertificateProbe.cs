// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Resources;
using JIM.Models.Connectors;
using JIM.Models.Localisation;
using JIM.Utilities;
using Serilog;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
namespace JIM.Connectors;

/// <summary>
/// Connects to a server over TLS purely to look at the certificate it presents, so a refused connection can be
/// reported as the certificate problem it is.
/// </summary>
/// <remarks>
/// This exists because the platform LDAP client tells JIM nothing when it refuses a certificate: the failure arrives
/// as "the server is unavailable", indistinguishable from an unreachable host. .NET's own TLS stack hands the
/// presented certificate to a callback, so JIM can look at a certificate it does not trust without ever trusting it:
/// the probe always refuses the connection, and is only ever used to explain a failure that already happened.
/// </remarks>
public static class ServerCertificateProbe
{
    /// <summary>
    /// The longest JIM waits for each issuer certificate it downloads while assembling the chain.
    /// </summary>
    private static readonly TimeSpan MaximumIssuerDownloadTime = TimeSpan.FromSeconds(5);

    /// <summary>The most a proxy's answer to <c>CONNECT</c> may run to before JIM stops reading it.</summary>
    private const int MaximumProxyAnswerLength = 16 * 1024;

    /// <summary>
    /// Fetches the certificate a server presents and works out why it would be refused.
    /// </summary>
    /// <param name="host">Host being connected to, as configured on the Connected System. The certificate's names are checked against this.</param>
    /// <param name="port">Port being connected to.</param>
    /// <param name="trustedCertificates">Certificates from the JIM certificate store. The server is trusted when any certificate in its chain is one of them.</param>
    /// <param name="timeout">How long to wait for the connection and handshake.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    /// <param name="serverDescription">What to call the far end in the remediation text, for example "directory server" or "SCIM service provider".</param>
    /// <param name="secureTransportName">The secure transport being attempted, for example "LDAPS" or "HTTPS", named in the remediation text.</param>
    /// <param name="handshakeFraming">How the TLS handshake has to reach the server; see <see cref="SecureEndpoint.HandshakeFraming"/>.</param>
    /// <param name="trustAnchorMustBeRoot">Whether only a root in the JIM certificate store makes the connection work; see <see cref="SecureEndpoint.TrustAnchorMustBeRoot"/>. When it does, the chain is judged as the connection will be: trusted only through a root the store holds.</param>
    /// <param name="proxy">For <see cref="SecureHandshakeFraming.Https"/> only: the outbound proxy to reach the server through. By default the one JIM's HTTP Connectors use (<see cref="HttpClient.DefaultProxy"/>); replaced in tests.</param>
    /// <returns>What the server presented and why it fails, or null when the server could not be reached at all, which is a different problem.</returns>
    public static ServerCertificateDiagnostic? Probe(
        string host,
        int port,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        TimeSpan timeout,
        ILogger logger,
        string serverDescription = "directory server",
        string secureTransportName = "LDAPS",
        SecureHandshakeFraming handshakeFraming = SecureHandshakeFraming.DirectTls,
        bool trustAnchorMustBeRoot = false,
        IWebProxy? proxy = null)
    {
        return Read(host, port, trustedCertificates, timeout, logger, serverDescription, secureTransportName, handshakeFraming, trustAnchorMustBeRoot, proxy)?.Diagnostic;
    }

    /// <summary>
    /// Fetches the certificate a server presents, returning both the description an administrator is shown and the
    /// certificates themselves, so that an administrator who decides to trust what they were just shown has exactly
    /// that certificate added rather than a copy taken earlier.
    /// </summary>
    /// <inheritdoc cref="Probe" path="/param"/>
    /// <returns>What the server presented, or null when it could not be reached at all, which is a different problem.</returns>
    public static ServerCertificateReading? Read(
        string host,
        int port,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        TimeSpan timeout,
        ILogger logger,
        string serverDescription = "directory server",
        string secureTransportName = "LDAPS",
        SecureHandshakeFraming handshakeFraming = SecureHandshakeFraming.DirectTls,
        bool trustAnchorMustBeRoot = false,
        IWebProxy? proxy = null)
    {
        X509Certificate2? presented = null;
        var sentByServer = new List<X509Certificate2>();
        var readAt = DateTime.UtcNow;

        try
        {
            // HTTPS is reached the way JIM's HTTP Connectors reach it, through the outbound proxy where one applies, so
            // that behind a TLS-inspecting proxy the certificate read is the one that refused the connection.
            var outboundProxy = handshakeFraming == SecureHandshakeFraming.Https
                ? ResolveOutboundProxy(host, port, proxy ?? HttpClient.DefaultProxy, logger)
                : null;

            using var client = new TcpClient();
            var (connectHost, connectPort) = outboundProxy != null ? (outboundProxy.Address.Host, outboundProxy.Address.Port) : (host, port);
            if (!client.ConnectAsync(connectHost, connectPort).Wait(timeout))
            {
                logger.Debug("ServerCertificateProbe: no response from {Host}:{Port} within the timeout, so this is a connectivity problem rather than a certificate one", LogSanitiser.Sanitise(connectHost), connectPort);
                return null;
            }

            // Without a deadline on reads, a server that accepts the connection and then says nothing would hold the
            // caller for ever; the timeout already bounds the connect, and now bounds every exchange after it too.
            client.ReceiveTimeout = client.SendTimeout = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);

            if (outboundProxy != null && !OpenProxyTunnel(client.GetStream(), outboundProxy, host, port, logger))
                return null;

            var transport = OpenHandshakeTransport(client.GetStream(), handshakeFraming, host, port, logger);
            if (transport == null)
                return DescribeWithoutCertificate(host, port, serverDescription, secureTransportName, trustAnchorMustBeRoot);

            using var sslStream = new SslStream(transport, false, (_, certificate, chain, _) =>
            {
                if (certificate != null)
                    presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

                // The platform hands the chain builder exactly what the server sent beyond its own certificate, in
                // the extra store; the chain's elements would mix in whatever the platform found elsewhere. Copied,
                // because the chain is disposed as soon as this callback returns.
                if (chain != null)
                    sentByServer.AddRange(chain.ChainPolicy.ExtraStore
                        .Where(sent => !string.Equals(sent.Thumbprint, presented?.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        .Select(sent => X509CertificateLoader.LoadCertificate(sent.RawData)));

                // Always refuse. Nothing is being connected to here; the handshake exists only to see the certificate.
                return false;
            });

            try
            {
                sslStream.AuthenticateAsClient(host);
            }
            catch (AuthenticationException)
            {
                // Expected: the callback above refuses every certificate, by design.
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException or ObjectDisposedException)
        {
            logger.Debug(ex, "ServerCertificateProbe: could not reach {Host}:{Port} to examine its certificate", LogSanitiser.Sanitise(host), port);
            return null;
        }

        try
        {
            if (presented == null)
                return DescribeWithoutCertificate(host, port, serverDescription, secureTransportName, trustAnchorMustBeRoot);

            // Downloading an issuer is bounded separately from the handshake, and more tightly: a dead address in a
            // certificate should not hold up the explanation of a failure for as long as the server itself may.
            // A connection that accepts only a root is judged as it will be made: counting anything else in the store
            // would call a certificate trusted that the connection then refuses.
            using var assembled = CertificateChainAssembler.Assemble(presented, sentByServer, trustedCertificates, Min(timeout, MaximumIssuerDownloadTime), trustAnchorMustBeRoot);
            var diagnostic = Describe(presented, host, port, assembled, serverDescription, trustAnchorMustBeRoot);
            var elements = assembled.Elements.Select(Describe).ToList();

            // Everything between the server's certificate and the root; a self-signed certificate is both, with
            // nothing between.
            var root = assembled.IsComplete ? elements[^1] : null;
            var intermediates = elements.Skip(1).Where(element => !ReferenceEquals(element, root)).ToList();

            return new ServerCertificateReading
            {
                Diagnostic = diagnostic,
                Chain = new PresentedServerCertificateChain
                {
                    Host = host,
                    Port = port,
                    ReadAt = readAt,
                    IsSelfSigned = diagnostic.IsSelfSigned,
                    Leaf = elements[0],
                    Intermediates = intermediates,
                    Root = root,
                    MissingIssuer = assembled.MissingIssuer
                }
            };
        }
        finally
        {
            presented?.Dispose();
            foreach (var certificate in sentByServer)
                certificate.Dispose();
        }
    }

    /// <summary>The outbound proxy an HTTPS request to the server goes through, and the credentials it is sent with.</summary>
    private sealed record OutboundProxy(Uri Address, NetworkCredential? Credential);

    /// <summary>
    /// The proxy an HTTPS request to the server goes through, as <see cref="HttpClient"/> decides it; null to connect
    /// directly.
    /// </summary>
    private static OutboundProxy? ResolveOutboundProxy(string host, int port, IWebProxy proxy, ILogger logger)
    {
        var destination = new UriBuilder(Uri.UriSchemeHttps, host, port).Uri;
        if (proxy.IsBypassed(destination))
            return null;

        var address = proxy.GetProxy(destination);
        if (address == null || address == destination)
            return null;

        // HttpClient tunnels through an http:// proxy, which is what is configured in practice; anything else is read
        // directly, as it was before proxies were considered at all.
        if (address.Scheme != Uri.UriSchemeHttp)
        {
            logger.Debug("ServerCertificateProbe: the outbound proxy for {Host}:{Port} is not an http:// proxy, so its certificate is read directly", LogSanitiser.Sanitise(host), port);
            return null;
        }

        return new OutboundProxy(address, proxy.Credentials?.GetCredential(address, "Basic"));
    }

    /// <summary>
    /// Asks the proxy for a tunnel to the server, as <see cref="HttpClient"/> does before an HTTPS request.
    /// </summary>
    /// <returns>Whether the proxy opened it. A proxy that refuses (wanting credentials JIM was not given, say) makes this a connectivity problem rather than a certificate one.</returns>
    private static bool OpenProxyTunnel(NetworkStream network, OutboundProxy proxy, string host, int port, ILogger logger)
    {
        var target = $"{new UriBuilder(Uri.UriSchemeHttps, host, port).Uri.Host}:{port}";
        var request = new StringBuilder($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
        if (proxy.Credential != null)
        {
            var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Credential.UserName}:{proxy.Credential.Password}"));
            request.Append($"Proxy-Authorization: Basic {credential}\r\n");
        }

        network.Write(Encoding.ASCII.GetBytes(request.Append("\r\n").ToString()));

        // Read one byte at a time so nothing after the answer is consumed: the TLS handshake follows on the same stream.
        var answer = new StringBuilder();
        var buffer = new byte[1];
        while (!answer.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (answer.Length >= MaximumProxyAnswerLength || network.Read(buffer) == 0)
            {
                logger.Debug("ServerCertificateProbe: the outbound proxy closed or overran its answer to CONNECT {Target}", LogSanitiser.Sanitise(target));
                return false;
            }

            answer.Append((char)buffer[0]);
        }

        var statusLine = answer.ToString().Split("\r\n")[0];
        var status = statusLine.Split(' ');
        if (status.Length >= 2 && status[1] == "200")
            return true;

        logger.Debug("ServerCertificateProbe: the outbound proxy refused CONNECT {Target}: {Status}", LogSanitiser.Sanitise(target), LogSanitiser.Sanitise(statusLine));
        return false;
    }

    /// <summary>
    /// The stream the TLS handshake runs over, framed the way the server expects to receive it.
    /// </summary>
    /// <returns>The stream, or null where the server has said it will not encrypt at all, so there is no certificate to see.</returns>
    /// <exception cref="IOException">The server does not speak the framing asked for.</exception>
    private static Stream? OpenHandshakeTransport(NetworkStream network, SecureHandshakeFraming handshakeFraming, string host, int port, ILogger logger)
    {
        if (handshakeFraming != SecureHandshakeFraming.TdsPreLogin)
            return network;

        // Microsoft SQL Server drops a connection that opens with a bare TLS handshake. A TDS 7.x client agrees
        // encryption in a PRELOGIN exchange first and then carries the handshake inside PRELOGIN packets, which is
        // the only way to reach the certificate; a server configured for TDS 8.0 strict encryption alone refuses this
        // too, and the original failure then stands unexplained, as it did before.
        if (TdsPreLogin.NegotiateEncryption(network) == TdsEncryption.NotSupported)
        {
            logger.Debug("ServerCertificateProbe: {Host}:{Port} answered PRELOGIN saying it does not support encryption, so it has no certificate to present", LogSanitiser.Sanitise(host), port);
            return null;
        }

        return new TdsPreLoginTlsStream(network);
    }

    private static ServerCertificateReading DescribeWithoutCertificate(string host, int port, string serverDescription, string secureTransportName, bool trustAnchorMustBeRoot)
    {
        var diagnostic = new ServerCertificateDiagnostic
        {
            Host = host,
            Port = port,
            TrustAnchorMustBeRoot = trustAnchorMustBeRoot,
            FailureReason = ServerCertificateFailureReason.NoCertificatePresented
        };
        diagnostic.SetRemediation(Message(nameof(ConnectorMessages.CertNoCertificate), ServerNoun.For(serverDescription), secureTransportName));
        return new ServerCertificateReading { Diagnostic = diagnostic };
    }

    /// <summary>
    /// Copies a certificate in the chain into the transferable form the trust decision acts on, DER encoded.
    /// </summary>
    private static PresentedServerCertificate Describe(AssembledCertificateChainElement element)
    {
        var certificate = element.Certificate;
        return new PresentedServerCertificate
        {
            Thumbprint = certificate.Thumbprint,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            Data = certificate.Export(X509ContentType.Cert),
            Source = element.Source,
            DownloadedFrom = element.DownloadedFrom
        };
    }

    /// <summary>
    /// Describes a certificate in the chain for display.
    /// </summary>
    private static ServerCertificateChainElement DescribeForDisplay(AssembledCertificateChainElement element)
    {
        var certificate = element.Certificate;
        return new ServerCertificateChainElement
        {
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            Thumbprint = certificate.Thumbprint,
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            IsCertificateAuthority = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(constraints => constraints.CertificateAuthority),
            IsSelfSigned = CertificateChainAssembler.IsSelfSigned(certificate),
            Source = element.Source,
            DownloadedFrom = element.DownloadedFrom
        };
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>
    /// Works out which check the certificate fails, in the order an administrator would act on them: a name mismatch
    /// is reported ahead of an untrusted issuer, because adding the certificate to the JIM certificate store fixes
    /// the second and not the first.
    /// </summary>
    private static ServerCertificateDiagnostic Describe(
        X509Certificate2 certificate,
        string host,
        int port,
        AssembledCertificateChain chain,
        string serverDescription,
        bool trustAnchorMustBeRoot)
    {
        var diagnostic = new ServerCertificateDiagnostic
        {
            Host = host,
            Port = port,
            TrustAnchorMustBeRoot = trustAnchorMustBeRoot,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            SubjectAlternativeNames = GetSubjectAlternativeNames(certificate),
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            Thumbprint = certificate.Thumbprint,
            SignatureAlgorithm = certificate.SignatureAlgorithm.FriendlyName,
            IsSelfSigned = CertificateChainAssembler.IsSelfSigned(certificate),
            Chain = chain.Elements.Select(DescribeForDisplay).ToList(),
            IsChainComplete = chain.IsComplete,
            MissingIssuer = chain.MissingIssuer,
            RootThumbprint = chain.Root?.Thumbprint,
            RootSubject = chain.Root?.Subject,
            IssuerThumbprint = chain.Elements.Count > 1 ? chain.Elements[1].Certificate.Thumbprint : null
        };

        var now = DateTime.UtcNow;
        if (diagnostic.ValidTo.HasValue && diagnostic.ValidTo.Value < now)
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.Expired;
            diagnostic.SetRemediation(Message(nameof(ConnectorMessages.CertExpired), ServerNoun.For(serverDescription)));
            return diagnostic;
        }

        if (diagnostic.ValidFrom.HasValue && diagnostic.ValidFrom.Value > now)
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.NotYetValid;
            diagnostic.SetRemediation(Message(nameof(ConnectorMessages.CertNotYetValid), ServerNoun.For(serverDescription)));
            return diagnostic;
        }

        if (!certificate.MatchesHostname(host, false, false))
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.NameMismatch;
            diagnostic.SetRemediation(Message(nameof(ConnectorMessages.CertNameMismatch), host));
            return diagnostic;
        }

        if (!chain.IsTrusted)
        {
            if (chain.Problem != null)
            {
                diagnostic.FailureReason = ServerCertificateFailureReason.InvalidChain;
                diagnostic.SetRemediation(Message(nameof(ConnectorMessages.CertChainProblem), chain.ProblemMessage!, ServerNoun.For(serverDescription)));
                return diagnostic;
            }

            diagnostic.FailureReason = ServerCertificateFailureReason.UntrustedIssuer;
            diagnostic.SetRemediation(trustAnchorMustBeRoot
                ? DescribeRootToTrust(diagnostic, chain, serverDescription)
                : DescribeWhatToTrust(diagnostic, chain, serverDescription));
            return diagnostic;
        }

        // Deliberately not judged on .NET's own policy errors: those are reported against the operating system's
        // trust anchors alone, so a certificate that JIM's certificate store legitimately vouches for still arrives
        // with a chain error. Everything JIM validates has now been checked, with those anchors taken into account.
        diagnostic.FailureReason = ServerCertificateFailureReason.None;
        return diagnostic;
    }

    /// <summary>
    /// What to trust to accept the server, most durable first: the root survives the renewal of everything beneath it.
    /// </summary>
    private static JimMessage DescribeWhatToTrust(ServerCertificateDiagnostic diagnostic, AssembledCertificateChain chain, string serverDescription)
    {
        if (diagnostic.IsSelfSigned)
            return Message(nameof(ConnectorMessages.CertSelfSigned), ServerNoun.For(serverDescription));

        if (chain.Root != null)
            return Message(nameof(ConnectorMessages.CertNothingTrustedWithRoot), PresentedServerCertificate.CommonNameOf(chain.Root.Subject));

        var highest = chain.Elements[^1].Certificate;
        return Message(nameof(ConnectorMessages.CertNothingTrustedIssuerMissing),
            PresentedServerCertificate.CommonNameOf(chain.MissingIssuer), PresentedServerCertificate.CommonNameOf(highest.Subject));
    }

    /// <summary>
    /// What to trust to accept the server where the connection accepts only a root: the root, which this connection
    /// cannot do without, whatever else is in the store.
    /// </summary>
    private static JimMessage DescribeRootToTrust(ServerCertificateDiagnostic diagnostic, AssembledCertificateChain chain, string serverDescription)
    {
        if (diagnostic.IsSelfSigned)
            return Message(nameof(ConnectorMessages.CertSelfSigned), ServerNoun.For(serverDescription));

        if (chain.Root != null)
            return Message(nameof(ConnectorMessages.CertRootNotTrusted), PresentedServerCertificate.CommonNameOf(chain.Root.Subject), ServerNoun.For(serverDescription));

        return Message(nameof(ConnectorMessages.CertRootNotTrustedIssuerMissing),
            PresentedServerCertificate.CommonNameOf(chain.MissingIssuer), PresentedServerCertificate.CommonNameOf(chain.Elements[^1].Certificate.Subject));
    }

    private static JimMessage Message(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);

    /// <summary>
    /// Reads the names the certificate was issued for out of its subject alternative name extension.
    /// </summary>
    private static List<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var names = new List<string>();

        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }

        return names;
    }
}
