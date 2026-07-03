// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for <see cref="ConfigurationDiffService"/>: scalar modify/add/remove, stable collection matching by database
/// id (not order), secret changes reported without disclosing the value, creation, and the no-change case.
/// </summary>
[TestFixture]
public class ConfigurationDiffServiceTests
{
    private JimApplication _jim = null!;
    private ConfigurationDiffService _diff = null!;
    private ConfigurationSnapshotService _snapshots = null!;
    private RoundTripCredentialProtection _protection = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _protection = new RoundTripCredentialProtection();
        _jim = new JimApplication(new Mock<IRepository>().Object) { CredentialProtection = _protection };
        _diff = _jim.ConfigurationDiffs;
        _snapshots = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public void Diff_ModifiedScalar_ReportsOldAndNewValue()
    {
        var old = Snap(Cs(description: "old desc"));
        var @new = Snap(Cs(description: "new desc"));

        var diff = _diff.Diff(old, @new, oldVersion: 1, newVersion: 2);

        var description = Find(diff.Root, "description")!;
        Assert.That(description.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Modified));
        Assert.That(description.OldValue, Is.EqualTo("old desc"));
        Assert.That(description.NewValue, Is.EqualTo("new desc"));
        Assert.That(diff.ModifiedCount, Is.EqualTo(1));
        Assert.That(diff.HasChanges, Is.True);
        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(2));
    }

    [Test]
    public void Diff_AddedCollectionItem_ReportsAddition()
    {
        var old = Snap(Cs(settings: [(1, "Server", "dc1")]));
        var @new = Snap(Cs(settings: [(1, "Server", "dc1"), (2, "Port", "636")]));

        var diff = _diff.Diff(old, @new);

        var settings = Find(diff.Root, "settingValues")!;
        var added = settings.Children!.Single(c => c.ItemId == 2);
        Assert.That(added.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Added));
        Assert.That(settings.Children!.Single(c => c.ItemId == 1).ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Unchanged));
        Assert.That(diff.AddedCount, Is.EqualTo(1));
    }

    [Test]
    public void Diff_RemovedCollectionItem_ReportsRemoval()
    {
        var old = Snap(Cs(settings: [(1, "Server", "dc1"), (2, "Port", "636")]));
        var @new = Snap(Cs(settings: [(1, "Server", "dc1")]));

        var diff = _diff.Diff(old, @new);

        var settings = Find(diff.Root, "settingValues")!;
        var removed = settings.Children!.Single(c => c.ItemId == 2);
        Assert.That(removed.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Removed));
        Assert.That(diff.RemovedCount, Is.EqualTo(1));
    }

    [Test]
    public void Diff_ReorderedCollectionWithOneChange_MatchesByIdNotOrder()
    {
        // Same items by id, reordered, with item 1's value changed. Only item 1 should be Modified.
        var old = Snap(Cs(settings: [(1, "Server", "x"), (2, "Port", "636")]));
        var @new = Snap(Cs(settings: [(2, "Port", "636"), (1, "Server", "z")]));

        var diff = _diff.Diff(old, @new);

        var settings = Find(diff.Root, "settingValues")!;
        Assert.That(settings.Children!.Single(c => c.ItemId == 1).ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Modified));
        Assert.That(settings.Children!.Single(c => c.ItemId == 2).ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Unchanged),
            "an item that only moved position must not be reported as changed");
        Assert.That(diff.ModifiedCount, Is.EqualTo(1));
    }

    [Test]
    public void Diff_ChangedSecret_ReportsModifiedWithoutDisclosingValue()
    {
        var old = Snap(CsWithSecret("old-password"));
        var @new = Snap(CsWithSecret("new-password"));

        var diff = _diff.Diff(old, @new);
        var json = System.Text.Json.JsonSerializer.Serialize(diff);

        var secret = Find(diff.Root, "Bind password")!;
        Assert.That(secret.IsSecret, Is.True);
        Assert.That(secret.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Modified), "a changed secret is detected via its keyed hash");
        Assert.That(secret.OldValue, Is.Null, "a secret's value (or hash) is never placed in the diff");
        Assert.That(secret.NewValue, Is.Null);
        Assert.That(json, Does.Not.Contain("old-password"));
        Assert.That(json, Does.Not.Contain("new-password"));
    }

    [Test]
    public void Diff_UnchangedSecret_ReportsUnchanged()
    {
        var diff = _diff.Diff(Snap(CsWithSecret("same")), Snap(CsWithSecret("same")));

        Assert.That(Find(diff.Root, "Bind password")!.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Unchanged));
    }

    [Test]
    public void Diff_IdenticalSnapshots_ReportsNoChanges()
    {
        var diff = _diff.Diff(Snap(Cs(description: "same")), Snap(Cs(description: "same")));

        Assert.That(diff.Root.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Unchanged));
        Assert.That(diff.HasChanges, Is.False);
        Assert.That(ConfigurationDiffService.Summarise(diff), Is.EqualTo("No changes"));
    }

    [Test]
    public void Diff_AgainstNull_ReportsCreation()
    {
        var diff = _diff.Diff(null, Snap(Cs(description: "first")), oldVersion: null, newVersion: 1);

        Assert.That(diff.Root.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Added));
        Assert.That(ConfigurationDiffService.Summarise(diff), Is.EqualTo("Created"));
        Assert.That(Find(diff.Root, "description")!.NewValue, Is.EqualTo("first"));
    }

    [Test]
    public void Summarise_Modification_ListsChangedTopLevelSections()
    {
        var diff = _diff.Diff(Snap(Cs(description: "old")), Snap(Cs(description: "new")));

        Assert.That(ConfigurationDiffService.Summarise(diff), Is.EqualTo("Description"));
    }

    [Test]
    public void Diff_ScheduleSteps_MatchByGuidIdNotStepIndex()
    {
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();

        // Two parallel steps share StepIndex 0, so only their Guid ids distinguish them. Between versions the two steps
        // swap position and step A's script path changes. Matching by Guid must see one modified step; matching by the
        // (non-unique) StepIndex would instead report churn.
        var v1 = SnapSchedule((stepA, 0, "/a.ps1"), (stepB, 0, "/b.ps1"));
        var v2 = SnapSchedule((stepB, 0, "/b.ps1"), (stepA, 0, "/a-changed.ps1"));

        var diff = _diff.Diff(v1, v2, oldVersion: 1, newVersion: 2);

        Assert.That(diff.AddedCount, Is.EqualTo(0), "no step was added; the steps were only reordered/edited");
        Assert.That(diff.RemovedCount, Is.EqualTo(0), "no step was removed; matching is by Guid id, not StepIndex");
        Assert.That(diff.ModifiedCount, Is.EqualTo(1), "exactly step A's script path changed");
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private ConfigurationSnapshot Snap(ConnectedSystem cs) => _snapshots.CreateSnapshot(cs, HashKey);

    private ConfigurationSnapshot SnapSchedule(params (Guid id, int stepIndex, string scriptPath)[] steps)
    {
        var schedule = new JIM.Models.Scheduling.Schedule { Id = Guid.NewGuid(), Name = "Sched" };
        foreach (var (id, stepIndex, scriptPath) in steps)
            schedule.Steps.Add(new JIM.Models.Scheduling.ScheduleStep
            {
                Id = id,
                StepIndex = stepIndex,
                StepType = JIM.Models.Scheduling.ScheduleStepType.PowerShell,
                ScriptPath = scriptPath
            });
        return _snapshots.CreateSnapshot(schedule, HashKey);
    }

    private static ConfigurationDiffNode? Find(ConfigurationDiffNode node, string key)
    {
        if (node.Key == key)
            return node;
        if (node.Children == null)
            return null;
        return node.Children.Select(c => Find(c, key)).FirstOrDefault(found => found != null);
    }

    private static ConnectedSystem Cs(string? description = null, (int settingId, string name, string? value)[]? settings = null)
    {
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4, Description = description };
        foreach (var (settingId, name, value) in settings ?? [])
            cs.SettingValues.Add(new ConnectedSystemSettingValue
            {
                Id = settingId,
                Setting = new ConnectorDefinitionSetting { Id = settingId, Name = name, Type = ConnectedSystemSettingType.String },
                StringValue = value
            });
        return cs;
    }

    private ConnectedSystem CsWithSecret(string secret)
    {
        var cs = new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4 };
        cs.SettingValues.Add(new ConnectedSystemSettingValue
        {
            Id = 1,
            Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Bind password", Type = ConnectedSystemSettingType.StringEncrypted },
            StringEncryptedValue = _protection.Protect(secret)
        });
        return cs;
    }

    /// <summary>Round-trip credential-protection test double, mirroring the one used by the snapshot service tests.</summary>
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
