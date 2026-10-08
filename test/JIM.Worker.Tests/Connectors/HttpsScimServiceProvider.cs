// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestScimServiceProvider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Serves a <see cref="MockScimProvider"/> over HTTPS from inside the test process, presenting whichever certificate a
/// test needs, on a free loopback port.
/// </summary>
/// <remarks>
/// The same Kestrel host and the same <see cref="ScimServiceProviderHost.MapScimServiceProvider"/> adapter the
/// Integration Scenario 015 container runs, minus the container: the SCIM Connector's TLS client is .NET's own HTTP
/// stack, so nothing outside the process is needed to give it a real handshake to validate.
/// </remarks>
internal sealed class HttpsScimServiceProvider : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HttpsScimServiceProvider(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>
    /// The loopback port the provider is listening on, chosen by the operating system.
    /// </summary>
    internal int Port { get; }

    /// <summary>
    /// Starts serving <paramref name="provider"/> over HTTPS, presenting <paramref name="certificate"/>, which must carry
    /// its private key.
    /// </summary>
    internal static async Task<HttpsScimServiceProvider> StartAsync(X509Certificate2 certificate, MockScimProvider provider)
    {
        // The empty builder reads no configuration files, so nothing in the test output directory (an appsettings.json
        // copied over from a referenced project, say) can add endpoints or change how Kestrel listens.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();

        // Port 0 has the operating system choose, so fixtures running at the same time never collide.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        var app = builder.Build();
        app.MapScimServiceProvider(provider);
        await app.StartAsync();

        return new HttpsScimServiceProvider(app, new Uri(app.Urls.Single()).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
