// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestGraphService;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// A stand-in for Microsoft Entra ID's token service and Microsoft Graph, for the Entra ID Connector's scale scenario
// (Scenario 040). It serves the same MockGraphTenant the unit suite drives in process. Inside the integration network
// login.microsoftonline.com and graph.microsoft.com resolve to this container, and the scenario adds its certificate
// to JIM's Trusted Certificates, so JIM runs unchanged and nothing it does reaches Microsoft.

var builder = WebApplication.CreateBuilder(args);

var settings = GraphServiceSettings.From(builder.Configuration);
var certificate = GraphServiceCertificate.Create([.. GraphServiceSettings.MicrosoftHostNames, settings.HostName]);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(settings.Port, listen =>
    {
        listen.UseHttps(certificate);
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

builder.Logging.AddSimpleConsole(console => console.SingleLine = true);

var app = builder.Build();
var tenant = new MockGraphTenant { RecordsWrites = true };

app.Logger.LogInformation("Stand-in Microsoft Graph service listening on port {Port} for {HostNames}; certificate thumbprint {Thumbprint}.",
    settings.Port, string.Join(", ", [.. GraphServiceSettings.MicrosoftHostNames, settings.HostName]), certificate.Thumbprint);

GraphServiceCertificate.Export(certificate, settings.CertificateExportPath, app.Logger);

app.MapGraphService(tenant);

await app.RunAsync();
