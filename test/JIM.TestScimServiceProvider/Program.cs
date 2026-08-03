// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.TestScimServiceProvider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// A SCIM 2.0 service provider for integration testing, serving the same MockScimProvider the unit suite
// drives in process. JIM is air-gap deployable and carries no third-party service dependency, so its
// test provider is written here rather than pulled as a container image.
//
// It serves HTTPS from a self-signed certificate it generates at startup: JIM refuses plain HTTP to
// anything but a loopback address, and in the integration stack the connector runs in a container and
// reaches this one by hostname. The scenario adds the certificate to JIM's trusted certificates, so the
// run exercises the trust path a customer with an internal certificate authority takes.

var builder = WebApplication.CreateBuilder(args);

var settings = ScimTestProviderSettings.From(builder.Configuration);
var certificate = ScimTestProviderCertificate.Create(settings.HostName);

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

var provider = settings.Configure(new MockScimProvider());

app.Logger.LogInformation(
    "SCIM test service provider listening on https://{HostName}:{Port} with {UserCount} user(s) and {GroupCount} group(s).",
    settings.HostName, settings.Port, settings.UserCount, settings.GroupCount);
app.Logger.LogInformation(
    "Certificate thumbprint {Thumbprint}, valid to {ValidTo}. Add it to JIM's Trusted Certificates to connect with Full Validation.",
    certificate.Thumbprint, certificate.NotAfter.ToString("u", CultureInfo.InvariantCulture));
app.Logger.LogInformation(
    "Bulk operations: {Bulk}.",
    provider.Options.SupportsBulk
        ? $"supported, {(provider.Options.BulkMaxOperations.HasValue ? $"maximum {provider.Options.BulkMaxOperations} per request" : "no stated limit")}"
        : "not supported");

// Written where the scenario can read it without scraping logs, so it can trust the certificate before
// pointing a Connected System at this provider.
ScimTestProviderCertificate.Export(certificate, settings.CertificateExportPath, app.Logger);

app.MapScimServiceProvider(provider);

await app.RunAsync();
