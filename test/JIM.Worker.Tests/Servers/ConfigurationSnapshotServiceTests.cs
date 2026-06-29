// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for <see cref="ConfigurationSnapshotService"/>: snapshot scope (what is and is not captured), secret redaction
/// (no secret material is ever serialised), and keyed-hash determinism and change detection.
/// </summary>
[TestFixture]
public class ConfigurationSnapshotServiceTests
{
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _service = null!;
    private RoundTripCredentialProtection _protection = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _protection = new RoundTripCredentialProtection();
        _jim = new JimApplication(new Mock<IRepository>().Object) { CredentialProtection = _protection };
        _service = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- Synchronisation Rule scope ------------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_SyncRule_CapturesScalarsAndChildCollectionsWithStableItemIds()
    {
        var mapping = new SyncRuleMapping { Id = 100, TargetMetaverseAttributeId = 5 };
        mapping.Sources.Add(new SyncRuleMappingSource { Id = 200, Order = 0, ConnectedSystemAttributeId = 9 });

        var rule = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = 3,
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectTypeId = 1
        };
        rule.AttributeFlowRules.Add(mapping);

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        Assert.That(snapshot.ObjectType, Is.EqualTo(ConfigurationSnapshotService.SyncRuleObjectType));
        Assert.That(snapshot.ObjectId, Is.EqualTo(42));
        Assert.That(snapshot.ObjectName, Is.EqualTo("HR Inbound"));
        Assert.That(snapshot.Root.NodeType, Is.EqualTo(ConfigurationSnapshotNodeType.Object));

        Assert.That(Child(snapshot.Root, "name")!.Value, Is.EqualTo("HR Inbound"));
        Assert.That(Child(snapshot.Root, "direction")!.Value, Is.EqualTo("Import"));
        Assert.That(Child(snapshot.Root, "enabled")!.Value, Is.EqualTo("true"));
        Assert.That(Child(snapshot.Root, "connectedSystemId")!.Value, Is.EqualTo("3"));

        var flows = Child(snapshot.Root, "attributeFlowRules")!;
        Assert.That(flows.NodeType, Is.EqualTo(ConfigurationSnapshotNodeType.Collection));
        Assert.That(flows.Children, Has.Count.EqualTo(1));

        var flow = flows.Children![0];
        Assert.That(flow.ItemId, Is.EqualTo(100), "Collection items must carry the stable DB id for diff matching.");
        Assert.That(Child(flow, "targetMetaverseAttributeId")!.Value, Is.EqualTo("5"));

        var sources = Child(flow, "sources")!;
        Assert.That(sources.Children, Has.Count.EqualTo(1));
        Assert.That(sources.Children![0].ItemId, Is.EqualTo(200));
        Assert.That(Child(sources.Children[0], "connectedSystemAttributeId")!.Value, Is.EqualTo("9"));
    }

    [Test]
    public void CreateSnapshot_SyncRule_EnrichesForeignKeysAndEnumsWithDisplayValues()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 100,
            TargetMetaverseAttributeId = 5,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 5, Name = "Display Name" },
            InboundValueProcessing = InboundValueProcessing.TreatWhitespaceAsNoValue
        };

        var rule = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 3,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" },
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectTypeId = 1
        };
        rule.AttributeFlowRules.Add(mapping);

        var snapshot = _service.CreateSnapshot(rule, HashKey);

        // Foreign keys keep the raw id for stable diffing, with a human-friendly "Name (id)" display value.
        var connectedSystem = Child(snapshot.Root, "connectedSystemId")!;
        Assert.That(connectedSystem.Value, Is.EqualTo("3"), "the raw FK id is kept for diffing");
        Assert.That(connectedSystem.DisplayValue, Is.EqualTo("AD (3)"));

        var flow = Child(snapshot.Root, "attributeFlowRules")!.Children![0];
        var targetAttribute = Child(flow, "targetMetaverseAttributeId")!;
        Assert.That(targetAttribute.Value, Is.EqualTo("5"));
        Assert.That(targetAttribute.DisplayValue, Is.EqualTo("Display Name (5)"));

        // Enums keep the raw name for diffing, with the name spaced into words for display.
        var processing = Child(flow, "inboundValueProcessing")!;
        Assert.That(processing.Value, Is.EqualTo("TreatWhitespaceAsNoValue"));
        Assert.That(processing.DisplayValue, Is.EqualTo("Treat Whitespace As No Value"));

        // A reference with no loaded navigation falls back to id-only (no display value), never crashes.
        var objectType = Child(snapshot.Root, "connectedSystemObjectTypeId")!;
        Assert.That(objectType.Value, Is.EqualTo("7"));
        Assert.That(objectType.DisplayValue, Is.Null);
    }

    [Test]
    public void CreateSnapshot_SyncRule_DoesNotSerialiseBacklinksOrParentNavigation()
    {
        var rule = new SyncRule
        {
            Id = 1,
            Name = "Rule",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = 2,
            // Activities backlink is left as the EF default (null!) on purpose; it must never be walked.
            ConnectedSystem = new ConnectedSystem { Id = 2, Name = "AD" }
        };

        var json = ConfigurationSnapshotService.Serialise(_service.CreateSnapshot(rule, HashKey));

        // The parent ConnectedSystem navigation and the Activities backlink are excluded by design; capture is by FK id.
        Assert.That(json, Does.Not.Contain("\"activities\""));
        Assert.That(json, Does.Not.Contain("\"connectedSystem\":{"));
        // The FK id is captured.
        Assert.That(json, Does.Contain("\"connectedSystemId\""));
    }

    // -- Connected System redaction ------------------------------------------------------------------------------------

    [Test]
    public void CreateSnapshot_ConnectedSystem_RedactsEncryptedSettingAndKeepsPlainSetting()
    {
        const string secret = "super-secret-pw";
        var cs = BuildConnectedSystemWithSecret(secret, out var ciphertext);

        var snapshot = _service.CreateSnapshot(cs, HashKey);
        var json = ConfigurationSnapshotService.Serialise(snapshot);

        // Hard requirement: neither the plaintext nor the ciphertext may ever appear in the snapshot.
        Assert.That(json, Does.Not.Contain(secret), "Plaintext secret must never be serialised.");
        Assert.That(json, Does.Not.Contain(ciphertext), "Encrypted secret value must never be serialised.");

        var settings = Child(snapshot.Root, "settingValues")!;
        var bindPassword = settings.Children!.Single(c => c.Label == "Bind password");
        Assert.That(bindPassword.IsSecret, Is.True);
        Assert.That(bindPassword.Value, Is.EqualTo(ExpectedHash(secret)), "Secret is represented by a keyed hash of the plaintext.");

        var server = settings.Children!.Single(c => c.Label == "Server");
        Assert.That(server.IsSecret, Is.False);
        Assert.That(server.Value, Is.EqualTo("ldaps://dc1"));
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_RedactsWhenSettingNavigationIsNotLoaded()
    {
        // Robust detection: a populated StringEncryptedValue is redacted even if the Setting navigation is null,
        // because StringEncryptedValue is only ever populated for encrypted settings.
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 11, StringEncryptedValue = _protection.Protect("pw") });

        var snapshot = _service.CreateSnapshot(cs, HashKey);
        var json = ConfigurationSnapshotService.Serialise(snapshot);

        Assert.That(json, Does.Not.Contain("pw\""));
        var node = Child(snapshot.Root, "settingValues")!.Children!.Single();
        Assert.That(node.IsSecret, Is.True);
        Assert.That(node.Value, Is.EqualTo(ExpectedHash("pw")));
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_SecretHashIsDeterministicAndDetectsChange()
    {
        var unchangedA = HashOfSecretInSnapshot("same-value");
        var unchangedB = HashOfSecretInSnapshot("same-value");
        var changed = HashOfSecretInSnapshot("different-value");

        Assert.That(unchangedA, Is.EqualTo(unchangedB), "Same secret and key must hash identically (so 'unchanged' is detectable).");
        Assert.That(changed, Is.Not.EqualTo(unchangedA), "A changed secret must hash differently (so 'changed' is detectable).");
    }

    [Test]
    public void CreateSnapshot_ConnectedSystem_ExcludesOperationalCollections()
    {
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4, PersistedConnectorData = "watermark-12345" };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 1, StringValue = "value" });

        var json = ConfigurationSnapshotService.Serialise(_service.CreateSnapshot(cs, HashKey));

        Assert.That(json, Does.Not.Contain("watermark-12345"), "PersistedConnectorData is opaque operational data and must be excluded.");
        Assert.That(json, Does.Not.Contain("\"objects\""));
        Assert.That(json, Does.Not.Contain("\"pendingExports\""));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static ConfigurationSnapshotNode? Child(ConfigurationSnapshotNode node, string key) =>
        node.Children?.FirstOrDefault(c => c.Key == key);

    private static string ExpectedHash(string plaintext)
    {
        using var hmac = new HMACSHA256(HashKey);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintext)));
    }

    private ConnectedSystem BuildConnectedSystemWithSecret(string secret, out string ciphertext)
    {
        var secretSetting = new ConnectorDefinitionSetting { Id = 1, Name = "Bind password", Type = ConnectedSystemSettingType.StringEncrypted };
        var serverSetting = new ConnectorDefinitionSetting { Id = 2, Name = "Server", Type = ConnectedSystemSettingType.String };
        ciphertext = _protection.Protect(secret)!;

        var cs = new ConnectedSystem { Id = 9, Name = "Active Directory", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 11, Setting = secretSetting, StringEncryptedValue = ciphertext });
        cs.SettingValues.Add(new ConnectedSystemSettingValue { Id = 12, Setting = serverSetting, StringValue = "ldaps://dc1" });
        return cs;
    }

    private string? HashOfSecretInSnapshot(string secret)
    {
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue
        {
            Id = 11,
            Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted },
            StringEncryptedValue = _protection.Protect(secret)
        });
        var snapshot = _service.CreateSnapshot(cs, HashKey);
        return Child(snapshot.Root, "settingValues")!.Children!.Single().Value;
    }

    /// <summary>
    /// A test double for credential protection that round-trips via a recognisable prefix and base64 so that the
    /// ciphertext does not literally contain the plaintext (letting tests assert both are absent from a snapshot).
    /// </summary>
    private sealed class RoundTripCredentialProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
