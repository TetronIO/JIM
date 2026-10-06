// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Serves certificates over HTTP, standing in for the addresses a PKI publishes in its certificates' Authority
/// Information Access extension.
/// </summary>
internal sealed class CertificateDownloadServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly Task _serving;

    /// <summary>
    /// Serves one certificate on a free port, at <see cref="Url"/>.
    /// </summary>
    internal CertificateDownloadServer(X509Certificate2 certificate)
        : this(FreePort(), new Dictionary<string, byte[]> { ["issuer.cer"] = certificate.RawData })
    {
    }

    private CertificateDownloadServer(int port, IReadOnlyDictionary<string, byte[]> files)
    {
        _files = files;
        BaseUrl = $"http://127.0.0.1:{port}/";
        Url = BaseUrl + files.Keys.First();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>
    /// Serves every file in <paramref name="directory"/> on <paramref name="port"/>, for certificates generated
    /// elsewhere with these addresses already written into them.
    /// </summary>
    internal static CertificateDownloadServer ServingDirectory(string directory, int port) =>
        new(port, Directory.GetFiles(directory).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes));

    internal string BaseUrl { get; }

    /// <summary>
    /// The address of the first certificate served.
    /// </summary>
    internal string Url { get; }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var name = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
                if (_files.TryGetValue(name, out var file))
                {
                    context.Response.ContentType = "application/pkix-cert";
                    await context.Response.OutputStream.WriteAsync(file);
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }

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
