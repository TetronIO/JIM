// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Interfaces;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors;

/// <summary>
/// The production <see cref="IServerCertificateReader"/>: one TLS handshake, made purely to look, and refused.
/// </summary>
public class ServerCertificateReader : IServerCertificateReader
{
    public ServerCertificateReading? Read(SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        return ServerCertificateProbe.Read(
            endpoint.Host,
            endpoint.Port,
            trustedCertificates,
            endpoint.Timeout,
            Log.Logger,
            endpoint.ServerDescription,
            endpoint.SecureTransportName);
    }
}
