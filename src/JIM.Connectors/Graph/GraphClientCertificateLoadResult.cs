// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors.Graph;

/// <summary>
/// What <see cref="GraphClientCertificate.Load"/> found. Exactly one of <see cref="Certificate"/> and
/// <see cref="Error"/> is set; <see cref="Warning"/> may accompany a certificate that loaded but needs attention soon.
/// Messages never include any part of the PFX or its password.
/// </summary>
internal sealed class GraphClientCertificateLoadResult : IDisposable
{
    public X509Certificate2? Certificate { get; private init; }

    public string? Error { get; private init; }

    public string? Warning { get; private init; }

    public static GraphClientCertificateLoadResult Loaded(X509Certificate2 certificate, string? warning = null) =>
        new() { Certificate = certificate, Warning = warning };

    public static GraphClientCertificateLoadResult Failed(string error) => new() { Error = error };

    public void Dispose() => Certificate?.Dispose();
}
