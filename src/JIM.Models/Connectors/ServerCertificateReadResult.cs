// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Connectors;

/// <summary>
/// The result of asking a Connected System's server what certificate it presents, without storing anything.
/// </summary>
/// <remarks>
/// Reading and trusting are separate operations on every surface. Reading tells an administrator what is there;
/// trusting is a decision they then make explicitly, naming the thumbprint they were shown.
/// </remarks>
public class ServerCertificateReadResult
{
    public ServerCertificateReadOutcome Outcome { get; init; }

    /// <summary>
    /// What the server presented and which check it fails. Null unless <see cref="Outcome"/> is
    /// <see cref="ServerCertificateReadOutcome.Read"/>.
    /// </summary>
    public ServerCertificateDiagnostic? Diagnostic { get; init; }

    /// <summary>
    /// When the server was asked, so the administrator knows how current the answer is.
    /// </summary>
    public DateTime? ReadAt { get; init; }

    /// <summary>
    /// A sentence explaining a non-<see cref="ServerCertificateReadOutcome.Read"/> outcome, suitable for showing.
    /// </summary>
    public string? Message
    {
        get => _message ?? Detail?.ToEnglish();
        init => _message = value;
    }

    private string? _message;

    /// <summary>
    /// The same explanation as a message the portal renders in the viewer's language; <see cref="Message"/> is its
    /// British English, which the REST API and logs keep reading. Null where there is no explanation to give.
    /// </summary>
    public JimMessage? Detail { get; init; }
}
