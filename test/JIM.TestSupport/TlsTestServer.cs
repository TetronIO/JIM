// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JIM.TestSupport;

/// <summary>
/// A minimal HTTPS server for tests: a real TLS handshake presenting the given certificate (and any intermediates),
/// then one HTTP/1.1 response per connection. Stands in for an identity provider whose certificate comes from an
/// internal certificate authority, without a web host or a container.
/// </summary>
public sealed class TlsTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly SslStreamCertificateContext _certificateContext;
    private readonly Func<string, string?> _respond;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private int _requestsServed;
    private int _connectionsAccepted;

    private TlsTestServer(X509Certificate2 serverCertificate, X509Certificate2[] intermediates, Func<string, string?> respond)
    {
        _certificateContext = SslStreamCertificateContext.Create(serverCertificate, new X509Certificate2Collection(intermediates), offline: true);
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>
    /// The port the server listens on, on the loopback address.
    /// </summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// How many requests completed a handshake and were answered.
    /// </summary>
    public int RequestsServed => Volatile.Read(ref _requestsServed);

    /// <summary>
    /// How many connections were opened, whether or not the client went on to trust the certificate.
    /// </summary>
    public int ConnectionsAccepted => Volatile.Read(ref _connectionsAccepted);

    /// <summary>
    /// The server's address under a given host name; use one the certificate does not carry to provoke a name mismatch.
    /// </summary>
    public Uri Address(string host = "localhost", string path = "/") => new($"https://{host}:{Port}{path}");

    /// <summary>
    /// Starts a server presenting <paramref name="serverCertificate"/> (which must carry its private key).
    /// </summary>
    /// <param name="serverCertificate">The certificate to present.</param>
    /// <param name="respond">The JSON body for a request path, or null for 404.</param>
    /// <param name="intermediates">Certificate authorities sent alongside it, as a server sends its issuing CA.</param>
    public static TlsTestServer Start(X509Certificate2 serverCertificate, Func<string, string?> respond, params X509Certificate2[] intermediates) =>
        new(serverCertificate, intermediates, respond);

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _connectionsAccepted);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _certificateContext }, _stopping.Token);

                var path = await ReadRequestPathAsync(tls);
                var body = path == null ? null : _respond(path);
                var status = body == null ? "404 Not Found" : "200 OK";
                var content = Encoding.UTF8.GetBytes(body ?? string.Empty);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(head, _stopping.Token);
                await tls.WriteAsync(content, _stopping.Token);
                await tls.FlushAsync(_stopping.Token);
                Interlocked.Increment(ref _requestsServed);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A refused handshake is the client deciding not to trust us: exactly what most tests here provoke.
            }
        }
    }

    private static async Task<string?> ReadRequestPathAsync(Stream stream)
    {
        var buffer = new byte[8192];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                return null;
            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var requestLine = received.ToString().Split("\r\n")[0].Split(' ');
        return requestLine.Length >= 2 ? requestLine[1] : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stopping.Dispose();
    }
}
