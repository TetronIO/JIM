// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// </remarks>
internal static class ConnectedSystemDraftSettings
{
    internal static void Apply(ConnectedSystem connectedSystem, IReadOnlyCollection<ConnectedSystemSettingValueDraft> draftSettingValues)
    {
        var draftsBySettingId = draftSettingValues.ToDictionary(d => d.SettingId);

        foreach (var settingValue in connectedSystem.SettingValues
            .Where(sv => sv.Setting != null && draftsBySettingId.ContainsKey(sv.Setting.Id)))
        {
            var draft = draftsBySettingId[settingValue.Setting.Id];

            if (settingValue.Setting.Type == ConnectedSystemSettingType.StringEncrypted)
            {
                if (draft.StringEncryptedValue != null)
                    settingValue.StringEncryptedValue = draft.StringEncryptedValue;
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
