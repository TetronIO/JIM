// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Tetron.PowerShellRemoting.Encryption;
using Tetron.PowerShellRemoting.Kerberos;
using Tetron.PowerShellRemoting.WSMan;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// One authenticated HTTP connection to a WinRM endpoint. WinRM authenticates the connection, not each request, so
/// this keeps to exactly one TCP connection with its own Kerberos context, and sends one request at a time on it.
/// Over http:// every request is Kerberos-encrypted and every reply must be; over https:// TLS protects them.
/// </summary>
internal sealed class WinRMConnection : IDisposable
{
    private readonly RemoteSessionOptions _options;
    private readonly KerberosLogin _login;
    private readonly RemoteSessionStatistics _statistics;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KerberosClientContext? _context;

    public WinRMConnection(RemoteSessionOptions options, KerberosLogin login, RemoteSessionStatistics statistics)
    {
        _options = options;
        _login = login;
        _statistics = statistics;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = ValidateServerCertificate
            }
        };
        _http = new HttpClient(handler) { Timeout = options.RequestTimeout };
    }

    /// <summary>Sends one SOAP envelope and returns the server's SOAP reply, authenticating the connection first if needed.</summary>
    public async Task<byte[]> SendAsync(byte[] envelope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (_context is not { IsEstablished: true })
                    await AuthenticateAsync(cancellationToken).ConfigureAwait(false);

                using var request = BuildRequest(envelope);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                // A 401 after authenticating usually means the server no longer knows this connection: it closed it
                // while it was idle (HTTP.sys does after two minutes) and the client opened a new one. The server
                // processed nothing, so the request goes again, once, on a newly authenticated connection.
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ResetContext();
                    if (attempt == 1)
                        continue;
                    throw new WSManFaultException(5, "AccessDenied",
                        "The WinRM server refused the request after Kerberos authentication (HTTP 401). The account may lack access to the session configuration.");
                }

                // WinRM returns SOAP faults as HTTP 500, still encrypted; anything else unexpected has no SOAP to read.
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.InternalServerError))
                    throw new WSManFaultException((uint)response.StatusCode, "HttpError",
                        $"The WinRM server answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

                if (!_options.UsesMessageEncryption)
                    return body;

                var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
                try
                {
                    return WinRMEncryptedMessage.Decode(body, contentType, _context!);
                }
                catch (WinRMEncryptionException error)
                {
                    // Say which response it was: the status and Content-Type are what tell one server's framing from another's.
                    throw new WinRMEncryptionException(
                        $"{error.Message} (HTTP {(int)response.StatusCode} {response.ReasonPhrase}, Content-Type '{contentType}')", error);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        ResetContext();
        _http.Dispose();
        _gate.Dispose();
    }

    private HttpRequestMessage BuildRequest(byte[] envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.ConnectionUri) { Version = HttpVersion.Version11 };
        if (_options.UsesMessageEncryption)
        {
            var encoded = WinRMEncryptedMessage.Encode(envelope, _context!);
            request.Content = new ByteArrayContent(encoded.Body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", encoded.ContentType);
        }
        else
        {
            request.Content = new ByteArrayContent(envelope);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "UTF-8" };
        }

        return request;
    }

    /// <summary>
    /// The Kerberos handshake, on an empty request as WinRM expects when messages will be encrypted: the client's
    /// token goes in <c>Authorization: Kerberos</c>, and the server's mutual-authentication token comes back in
    /// <c>WWW-Authenticate</c>.
    /// </summary>
    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        ResetContext();
        var credential = _login.Current;
        byte[]? token;
        try
        {
            _context = KerberosClientContext.Create(credential, _options.ServiceHost);
            token = _context.Step(ReadOnlySpan<byte>.Empty);
        }
        catch (KerberosException error) when (error.IsCredentialExpired)
        {
            // The session's ticket-granting ticket has run out (ten hours, by default): log in again and carry on.
            ResetContext();
            _context = KerberosClientContext.Create(_login.Renew(credential), _options.ServiceHost);
            token = _context.Step(ReadOnlySpan<byte>.Empty);
        }
        for (var round = 0; round < 4 && token is not null; round++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.ConnectionUri)
            {
                Version = HttpVersion.Version11,
                Content = new ByteArrayContent([])
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Kerberos", Convert.ToBase64String(token));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var reply = response.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Kerberos", StringComparison.OrdinalIgnoreCase))?.Parameter;
            if (response.StatusCode != HttpStatusCode.OK && string.IsNullOrEmpty(reply))
                throw new WSManFaultException(5, "AccessDenied",
                    $"The WinRM server refused Kerberos authentication for {_options.UserPrincipalName} (HTTP {(int)response.StatusCode}).");

            token = string.IsNullOrEmpty(reply) || _context.IsEstablished ? null : _context.Step(Convert.FromBase64String(reply));
        }

        if (!_context.IsEstablished)
            throw new WSManFaultException(5, "AccessDenied",
                "The WinRM server did not complete mutual Kerberos authentication, so its identity is unproven; nothing was sent.");
        _statistics.RecordConnectionOpened();
    }

    private void ResetContext()
    {
        _context?.Dispose();
        _context = null;
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None || _options.TrustedCertificates.Count == 0)
            return false;

        // A name mismatch or a missing certificate is never waived; only an unknown issuer may be vouched for by a
        // CA the caller trusts, in addition to the system's.
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(_options.TrustedCertificates);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(certificate);
        return custom.Build(leaf);
    }
}
