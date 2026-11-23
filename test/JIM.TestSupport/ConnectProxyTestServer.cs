// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JIM.TestSupport;

/// <summary>
/// A minimal outbound HTTP proxy for tests: it answers each <c>CONNECT</c> and then relays bytes both ways to one
/// fixed loopback port, whatever host the client named. Behind it, a <see cref="TlsTestServer"/> presenting a
/// certificate JIM does not trust behaves as a TLS-inspecting proxy does, without a proxy product or a container.
/// </summary>
public sealed class ConnectProxyTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _relayToPort;
    private readonly string _answer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly List<ProxiedRequest> _requests = [];

    private ConnectProxyTestServer(int relayToPort, string answer)
    {
        _relayToPort = relayToPort;
        _answer = answer;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>A <c>CONNECT</c> the proxy received: the target the client asked for and the proxy credentials it sent.</summary>
    public sealed record ProxiedRequest(string Target, string? ProxyAuthorization);

    /// <summary>The proxy's address, as an outbound proxy setting names it.</summary>
    public Uri Address => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    /// <summary>Every <c>CONNECT</c> received so far, in order.</summary>
    public IReadOnlyList<ProxiedRequest> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToList();
        }
    }

    /// <summary>Starts a proxy that opens every tunnel, relaying it to <paramref name="relayToPort"/> on the loopback address.</summary>
    public static ConnectProxyTestServer Start(int relayToPort) => new(relayToPort, "200 Connection established");

    /// <summary>Starts a proxy that answers every <c>CONNECT</c> with <paramref name="status"/> (for example <c>407 Proxy Authentication Required</c>) and opens nothing.</summary>
    public static ConnectProxyTestServer Refusing(string status) => new(0, status);

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

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                if (head == null)
                    return;

                var lines = head.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                var authorisation = lines.Skip(1)
                    .Where(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line["Proxy-Authorization:".Length..].Trim())
                    .FirstOrDefault();
                lock (_requests)
                    _requests.Add(new ProxiedRequest(requestLine.Length >= 2 ? requestLine[1] : string.Empty, authorisation));

                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {_answer}\r\n\r\n"), _stopping.Token);
                if (!_answer.StartsWith("200", StringComparison.Ordinal))
                    return;

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, _relayToPort, _stopping.Token);
                var upstreamStream = upstream.GetStream();
                await Task.WhenAny(
                    stream.CopyToAsync(upstreamStream, _stopping.Token),
                    upstreamStream.CopyToAsync(stream, _stopping.Token));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client closing the tunnel, which every probe does once it has seen the certificate.
            }
        }
    }

    /// <summary>Reads the request line and headers, one byte at a time so nothing after them is consumed.</summary>
    private async Task<string?> ReadHeadAsync(Stream stream)
    {
        var received = new StringBuilder();
        var buffer = new byte[1];
        while (!received.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer, _stopping.Token) == 0)
                return null;
            received.Append((char)buffer[0]);
        }

        return received.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }

        _stopping.Dispose();
    }
}
