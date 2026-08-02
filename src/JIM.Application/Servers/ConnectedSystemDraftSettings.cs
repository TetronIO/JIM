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
/// Encrypted settings are never taken from a draft: nothing needed to work out where a system connects is a
/// secret, and the draft model deliberately has no encrypted-value field, so a credential always comes from the
/// saved value. Callers must load the Connected System without change tracking so applying drafts cannot reach
/// the database.
/// </remarks>
internal static class ConnectedSystemDraftSettings
{
    internal static void Apply(ConnectedSystem connectedSystem, IReadOnlyCollection<ConnectedSystemSettingValueDraft> draftSettingValues)
    {
        var draftsBySettingId = draftSettingValues.ToDictionary(d => d.SettingId);

        foreach (var settingValue in connectedSystem.SettingValues
            .Where(sv => sv.Setting?.Type != ConnectedSystemSettingType.StringEncrypted &&
                         sv.Setting != null && draftsBySettingId.ContainsKey(sv.Setting.Id)))
        {
            var draft = draftsBySettingId[settingValue.Setting.Id];

            if (draft.StringValue != null)
                settingValue.StringValue = draft.StringValue;
            if (draft.IntValue.HasValue)
                settingValue.IntValue = draft.IntValue.Value;
            if (draft.CheckboxValue.HasValue)
                settingValue.CheckboxValue = draft.CheckboxValue.Value;
        }
    }
}
