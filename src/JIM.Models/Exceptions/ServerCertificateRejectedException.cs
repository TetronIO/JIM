// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Localisation;

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

    /// <summary>
    /// A rejection whose words are held as a message, so the portal can show them in the viewer's language (#3030);
    /// <see cref="Exception.Message"/> is its British English, which Activities and logs keep reading.
    /// </summary>
    public ServerCertificateRejectedException(JimMessage detail, ServerCertificateDiagnostic diagnostic, Exception? innerException = null)
        : base(detail.ToEnglish(), innerException)
    {
        Diagnostic = diagnostic;
        Detail = detail;
    }

    /// <summary>The rejection as a message the portal can render in the viewer's language; null where only English text exists.</summary>
    public JimMessage? Detail { get; }
}
