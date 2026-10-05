// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Serves one certificate over HTTP, standing in for the address a PKI publishes in its certificates'
/// Authority Information Access extension.
/// </summary>
internal sealed class CertificateDownloadServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _certificate;
    private readonly Task _serving;

    internal CertificateDownloadServer(X509Certificate2 certificate)
    {
        _certificate = certificate.RawData;
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/issuer.cer";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    internal string Url { get; }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                context.Response.ContentType = "application/pkix-cert";
                await context.Response.OutputStream.WriteAsync(_certificate);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
        }
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        _serving.Wait(TimeSpan.FromSeconds(5));
    }
}
