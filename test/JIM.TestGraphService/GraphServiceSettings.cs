// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace JIM.TestGraphService;

/// <summary>How a run of the stand-in Microsoft Graph service is configured, from environment variables or command line.</summary>
public sealed class GraphServiceSettings
{
    /// <summary>The names Microsoft Entra ID's token service and Microsoft Graph answer on, which the stand-in answers for.</summary>
    public static readonly string[] MicrosoftHostNames = ["login.microsoftonline.com", "graph.microsoft.com"];

    /// <summary>443, as JIM reaches Microsoft Graph on no other port.</summary>
    public int Port { get; private init; } = 443;

    /// <summary>The stand-in's own name on the Docker network, which the scenario's harness reaches it by.</summary>
    public string HostName { get; private init; } = "graph-service";

    /// <summary>Where the public certificate is written for the scenario to trust. Empty writes none.</summary>
    public string? CertificateExportPath { get; private init; }

    public static GraphServiceSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new GraphServiceSettings
        {
            Port = configuration["GRAPH_PORT"] is { Length: > 0 } port ? int.Parse(port, CultureInfo.InvariantCulture) : 443,
            HostName = configuration["GRAPH_HOSTNAME"] is { Length: > 0 } host ? host : "graph-service",
            CertificateExportPath = configuration["GRAPH_CERTIFICATE_PATH"]
        };
    }
}
