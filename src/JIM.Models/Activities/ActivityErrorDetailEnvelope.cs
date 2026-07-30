// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;

namespace JIM.Models.Activities;

/// <summary>
/// The serialised shape of <see cref="Activity.ErrorDetail"/>. <see cref="Kind"/> names the payload so a reader can
/// recognise what it is looking at before parsing further, leaving room for other kinds of structured failure detail.
/// </summary>
public class ActivityErrorDetailEnvelope
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Present when <see cref="Kind"/> is <see cref="ActivityErrorDetail.ServerCertificateKind"/>.
    /// </summary>
    public ServerCertificateDiagnostic? ServerCertificate { get; set; }
}
