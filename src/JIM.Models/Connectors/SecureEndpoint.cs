// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// The encrypted endpoint a Connected System's own settings point at, together with how to describe the far end to
/// an administrator.
/// </summary>
/// <remarks>
/// The connector resolves this from its settings so that no caller ever supplies a host and port: an endpoint that
/// took them from a request body would let anyone with API access make JIM connect wherever they liked.
/// </remarks>
/// <param name="Host">The host to connect to, which the certificate's names are checked against.</param>
/// <param name="Port">The port to connect to.</param>
/// <param name="Timeout">How long to wait for the connection and handshake, from the system's own timeout setting.</param>
/// <param name="ServerDescription">What to call the far end, for example "directory server" or "SCIM service provider".</param>
/// <param name="SecureTransportName">The secure transport in use, for example "LDAPS" or "HTTPS".</param>
public sealed record SecureEndpoint(
    string Host,
    int Port,
    TimeSpan Timeout,
    string ServerDescription,
    string SecureTransportName);
