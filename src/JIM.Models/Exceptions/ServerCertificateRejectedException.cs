// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;

namespace JIM.Models.Exceptions;

/// <summary>
/// Thrown when a connection is refused because of the certificate the server presented, carrying what that
/// certificate was so the failure can be reported with something an administrator can act on.
/// </summary>
public class ServerCertificateRejectedException : Exception
{
    public ServerCertificateDiagnostic Diagnostic { get; }

    public ServerCertificateRejectedException(string message, ServerCertificateDiagnostic diagnostic, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostic = diagnostic;
    }
}
