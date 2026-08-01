// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a Connected System's server is presenting right now. Nothing is stored by reading it.
/// </summary>
public class ServerCertificateResponse
{
    /// <summary>
    /// The certificate the server presented and which check it fails, judged with the JIM certificate store taken
    /// into account.
    /// </summary>
    public ServerCertificateDiagnostic Certificate { get; set; } = null!;

    /// <summary>
    /// When the server was asked, so a caller knows how current the answer is.
    /// </summary>
    public DateTime ReadAt { get; set; }
}

/// <summary>
/// Connectivity settings entered but not yet saved, sent so JIM looks at the endpoint the caller is configuring
/// rather than the one last saved.
/// </summary>
/// <remarks>
/// JIM refuses to save settings that fail validation, and a certificate JIM does not trust is a validation failure,
/// so an administrator configuring a new Connected System has an address on screen and nothing in the database.
/// Sending it here is what lets them see and trust the certificate before the system can be saved. Keyed by
/// <c>ConnectorDefinitionSetting</c> id, matching the settings update request. Never persisted, and never applied to
/// encrypted settings.
/// </remarks>
public class ServerCertificateDraftSettings
{
    public Dictionary<int, ConnectedSystemSettingValueUpdate>? SettingValues { get; set; }

    /// <summary>
    /// Maps the request's setting values into the application layer's draft type.
    /// </summary>
    public static IReadOnlyCollection<ConnectedSystemSettingValueDraft>? ToDrafts(Dictionary<int, ConnectedSystemSettingValueUpdate>? settingValues)
    {
        if (settingValues == null || settingValues.Count == 0)
            return null;

        return settingValues.Select(sv => new ConnectedSystemSettingValueDraft
        {
            SettingId = sv.Key,
            StringValue = sv.Value.StringValue,
            IntValue = sv.Value.IntValue,
            CheckboxValue = sv.Value.CheckboxValue
        }).ToList();
    }
}

/// <summary>
/// A request to read the certificate a Connected System's server presents, using settings that have not been saved.
/// </summary>
public class ReadServerCertificateRequest : ServerCertificateDraftSettings;

/// <summary>
/// A request to trust the certificate a Connected System's server presents.
/// </summary>
/// <remarks>
/// There is deliberately no host or port here: the endpoint is always derived by the Connected System's own connector
/// from that system's settings, saved or draft, so a caller cannot name an address directly.
/// </remarks>
public class TrustServerCertificateRequest : ServerCertificateDraftSettings
{
    /// <summary>
    /// The thumbprint being trusted, as read from the server. Required: JIM will not trust whatever a server happens
    /// to be presenting. Matched against the certificate the server presents now and against the authority that
    /// issued it; whichever matches is what gets trusted. Spaces and colons between the pairs are ignored.
    /// </summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Why the certificate is being trusted, recorded on the audit Activity. Optional; JIM records a sentence naming
    /// the Connected System when none is given.
    /// </summary>
    public string? ChangeReason { get; set; }
}

/// <summary>
/// The outcome of trusting the certificate a Connected System's server presents.
/// </summary>
public class TrustServerCertificateResponse
{
    public ServerCertificateTrustOutcome Outcome { get; set; }

    /// <summary>
    /// A sentence explaining the outcome.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The certificate as it now sits in the JIM certificate store. Set when the outcome is
    /// <see cref="ServerCertificateTrustOutcome.Trusted"/>.
    /// </summary>
    public TrustedCertificateDetailDto? Certificate { get; set; }

    /// <summary>
    /// The thumbprint the caller named, and the one the server is presenting now. Both are set when the outcome is
    /// <see cref="ServerCertificateTrustOutcome.ThumbprintMismatch"/>, so the two can be compared rather than the
    /// caller being told only that something changed.
    /// </summary>
    public string? ExpectedThumbprint { get; set; }

    public string? PresentedThumbprint { get; set; }

    public static TrustServerCertificateResponse FromResult(ServerCertificateTrustResult result)
    {
        return new TrustServerCertificateResponse
        {
            Outcome = result.Outcome,
            Message = result.Message,
            Certificate = result.Certificate == null ? null : TrustedCertificateDetailDto.FromEntity(result.Certificate),
            ExpectedThumbprint = result.ExpectedThumbprint,
            PresentedThumbprint = result.PresentedThumbprint
        };
    }
}
