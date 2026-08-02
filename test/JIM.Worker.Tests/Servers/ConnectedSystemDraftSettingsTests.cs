// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers <see cref="ConnectedSystemDraftSettings.Apply"/>, the shared on-screen-over-saved setting merge used by
/// both the server certificate read/trust actions and Discover Domain Controllers (issue #1167). End-to-end draft
/// behaviour through the certificate path is covered by <see cref="ServerCertificateTrustTests"/>; this class
/// pins the merge rules themselves, since discovery has no unit-test seam to observe which settings reach the
/// Connector.
/// </summary>
[TestFixture]
public class ConnectedSystemDraftSettingsTests
{
    private static ConnectedSystem CreateConnectedSystem()
    {
        return new ConnectedSystem
        {
            Id = 7,
            Name = "Test Connected System",
            SettingValues =
            [
                new ConnectedSystemSettingValue
                {
                    Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Host", Type = ConnectedSystemSettingType.String },
                    StringValue = "saved-host.example.org"
                },
                new ConnectedSystemSettingValue
                {
                    Setting = new ConnectorDefinitionSetting { Id = 2, Name = "Port", Type = ConnectedSystemSettingType.Integer },
                    IntValue = 389
                },
                new ConnectedSystemSettingValue
                {
                    Setting = new ConnectorDefinitionSetting { Id = 3, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted },
                    StringEncryptedValue = "saved-secret"
                }
            ]
        };
    }

    [Test]
    public void Apply_DraftsForPlainSettings_OverrideSavedValues()
    {
        var connectedSystem = CreateConnectedSystem();

        ConnectedSystemDraftSettings.Apply(connectedSystem,
        [
            new ConnectedSystemSettingValueDraft { SettingId = 1, StringValue = "draft-host.example.org" },
            new ConnectedSystemSettingValueDraft { SettingId = 2, IntValue = 636 }
        ]);

        Assert.That(connectedSystem.SettingValues.Single(sv => sv.Setting.Id == 1).StringValue, Is.EqualTo("draft-host.example.org"));
        Assert.That(connectedSystem.SettingValues.Single(sv => sv.Setting.Id == 2).IntValue, Is.EqualTo(636));
    }

    [Test]
    public void Apply_DraftForAnEncryptedSetting_LeavesTheSavedValueAlone()
    {
        var connectedSystem = CreateConnectedSystem();

        ConnectedSystemDraftSettings.Apply(connectedSystem,
        [
            new ConnectedSystemSettingValueDraft { SettingId = 3, StringValue = "attacker-or-accident" }
        ]);

        var encrypted = connectedSystem.SettingValues.Single(sv => sv.Setting.Id == 3);
        Assert.That(encrypted.StringEncryptedValue, Is.EqualTo("saved-secret"));
        Assert.That(encrypted.StringValue, Is.Null);
    }

    [Test]
    public void Apply_DraftWithNoMatchingSetting_ChangesNothing()
    {
        var connectedSystem = CreateConnectedSystem();

        ConnectedSystemDraftSettings.Apply(connectedSystem,
        [
            new ConnectedSystemSettingValueDraft { SettingId = 99, StringValue = "no such setting" }
        ]);

        Assert.That(connectedSystem.SettingValues.Single(sv => sv.Setting.Id == 1).StringValue, Is.EqualTo("saved-host.example.org"));
    }
}
