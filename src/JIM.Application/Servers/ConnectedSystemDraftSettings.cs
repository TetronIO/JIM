// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Staging;
namespace JIM.Application.Servers;

/// <summary>
/// Applies connectivity settings an administrator has entered on screen but not yet saved over a Connected
/// System's saved setting values, so read-only preflight actions (reading a server certificate, discovering
/// domain controllers) can look at the endpoint the administrator is actually configuring rather than whatever
/// was last saved. Shared by <see cref="CertificateServer"/> and <see cref="ConnectedSystemServer"/>.
/// </summary>
/// <remarks>
/// An encrypted setting (a credential) is only ever taken from a draft's explicit
/// <see cref="ConnectedSystemSettingValueDraft.StringEncryptedValue"/> channel, supplied by flows that must
/// authenticate with what the administrator has typed (Discover Domain Controllers on a system whose settings
/// have never been saved); a plain <see cref="ConnectedSystemSettingValueDraft.StringValue"/> draft never
/// touches one. Callers must load the Connected System without change tracking so applying drafts cannot reach
/// the database.
/// <para>
/// A drafted credential is what the administrator typed, in clear, and a Connector only ever accepts an encrypted
/// one (#2680), so it is encrypted as it is applied. One already encrypted (the saved value posted back untouched)
/// is applied as it is.
/// </para>
/// </remarks>
internal static class ConnectedSystemDraftSettings
{
    /// <param name="connectedSystem">The Connected System, loaded without change tracking.</param>
    /// <param name="draftSettingValues">The values entered on screen.</param>
    /// <param name="credentialProtection">Resolves the credential protection that encrypts a drafted credential; only called when there is one.</param>
    internal static void Apply(
        ConnectedSystem connectedSystem,
        IReadOnlyCollection<ConnectedSystemSettingValueDraft> draftSettingValues,
        Func<ICredentialProtectionService> credentialProtection)
    {
        var draftsBySettingId = draftSettingValues.ToDictionary(d => d.SettingId);

        foreach (var settingValue in connectedSystem.SettingValues
            .Where(sv => sv.Setting != null && draftsBySettingId.ContainsKey(sv.Setting.Id)))
        {
            var draft = draftsBySettingId[settingValue.Setting.Id];

            if (settingValue.Setting.Type == ConnectedSystemSettingType.StringEncrypted)
            {
                if (draft.StringEncryptedValue != null)
                    settingValue.StringEncryptedValue = draft.StringEncryptedValue.Length == 0
                        ? draft.StringEncryptedValue
                        : credentialProtection().Protect(draft.StringEncryptedValue);
                continue;
            }

            if (draft.StringValue != null)
                settingValue.StringValue = draft.StringValue;
            if (draft.IntValue.HasValue)
                settingValue.IntValue = draft.IntValue.Value;
            if (draft.CheckboxValue.HasValue)
                settingValue.CheckboxValue = draft.CheckboxValue.Value;
        }
    }
}
