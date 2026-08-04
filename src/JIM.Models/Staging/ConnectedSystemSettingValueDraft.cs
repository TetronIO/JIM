// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A Connected System setting value that has been entered but not saved.
/// </summary>
/// <remarks>
/// JIM refuses to save settings that fail validation, and a certificate JIM does not trust is a validation failure.
/// An administrator configuring a new Connected System therefore has the address on screen and nothing in the
/// database, so anything that reads only saved settings would look at the wrong server, or at no server at all.
/// Drafts exist so the certificate an administrator is shown is the one presented by the endpoint they just typed.
/// <para>
/// This does not widen what an administrator can reach: saving a Connected System already opens a connection to
/// whatever address its settings name, so the same role can already make JIM connect anywhere. Drafts are never
/// persisted. A plain <see cref="StringValue"/> draft is never applied to an encrypted setting; a credential
/// only travels via the explicit <see cref="StringEncryptedValue"/> channel, and only flows that genuinely
/// need to authenticate (Discover Domain Controllers) supply it. The certificate read/trust actions never do:
/// nothing needed to work out where a system connects is a secret.
/// </para>
/// </remarks>
public class ConnectedSystemSettingValueDraft
{
    /// <summary>
    /// The <see cref="ConnectorDefinitionSetting"/> this value is for.
    /// </summary>
    public int SettingId { get; set; }

    public string? StringValue { get; set; }

    /// <summary>
    /// An unsaved value for an encrypted setting (a credential), supplied only by flows that must
    /// authenticate with what the administrator has typed, e.g. discovering domain controllers on a
    /// Connected System whose settings have never been saved. Ignored for non-encrypted settings.
    /// </summary>
    public string? StringEncryptedValue { get; set; }

    public int? IntValue { get; set; }

    public bool? CheckboxValue { get; set; }
}
