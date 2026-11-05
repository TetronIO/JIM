// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A Run Profile's enabled state (#2740): what each surface says about a disabled Run Profile, and the rule for enabling
/// one again. A hierarchy refresh that removes the Partition a Run Profile targets disables it and clears its Partition,
/// and enabling it again with no Partition would quietly turn it into one that reads every selected Partition. That is
/// the widening the refresh disabled it to prevent, so enabling it has to say what it reads.
/// </summary>
[TestFixture]
public class RunProfileEnablementTests
{
    private static ConnectedSystemRunProfile RunProfile(bool enabled = true, ConnectedSystemRunProfileDisabledReason? reason = null,
        string? partitionName = null, ConnectedSystemPartition? partition = null) => new()
    {
        Id = 7,
        Name = "Full Import (Glitterband)",
        RunType = ConnectedSystemRunType.FullImport,
        Enabled = enabled,
        DisabledReason = reason,
        DisabledPartitionName = partitionName,
        Partition = partition
    };

    private static ConnectedSystemRunProfileEnabledState DisabledByRemovedPartition() =>
        new(Enabled: false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");

    private static ConnectedSystemPartition Partition() => new() { Id = 3, Name = "dc=yellowstone,dc=local", ExternalId = "dc=yellowstone,dc=local", Selected = true };

    [Test]
    public void NewRunProfile_IsEnabled()
    {
        Assert.That(new ConnectedSystemRunProfile().Enabled, Is.True, "a Run Profile is created enabled, and existing ones migrate enabled");
    }

    [Test]
    public void DescribeWhyDisabled_Enabled_ReturnsNull()
    {
        Assert.That(RunProfile().DescribeWhyDisabled(), Is.Null);
    }

    [Test]
    public void DescribeWhyDisabled_DisabledByHand_SaysAnAdministratorDisabledIt()
    {
        var runProfile = RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.Manual);

        Assert.That(runProfile.DescribeWhyDisabled(), Is.EqualTo("An administrator disabled it."));
    }

    [Test]
    public void DescribeWhyDisabled_DisabledByARemovedPartition_NamesThePartition()
    {
        var runProfile = RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");

        Assert.That(runProfile.DescribeWhyDisabled(), Is.EqualTo(
            "A hierarchy refresh removed the partition it targeted, 'dc=glitterband,dc=local', because the Connected System no longer has it."));
    }

    [Test]
    public void DescribeRunRefusal_DisabledByARemovedPartition_NamesTheRunProfileTheReasonAndTheRemedy()
    {
        var runProfile = RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");

        Assert.That(RunProfileEnablement.DescribeRunRefusal(runProfile), Is.EqualTo(
            "Run Profile 'Full Import (Glitterband)' is disabled, so it cannot run. A hierarchy refresh removed the partition it targeted, " +
            "'dc=glitterband,dc=local', because the Connected System no longer has it. Enable it again, choosing the partition it reads, to run it."));
    }

    [Test]
    public void DescribeRunRefusal_DisabledByHand_NamesTheRunProfileAndTheRemedy()
    {
        var runProfile = RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.Manual);

        Assert.That(RunProfileEnablement.DescribeRunRefusal(runProfile), Is.EqualTo(
            "Run Profile 'Full Import (Glitterband)' is disabled, so it cannot run. An administrator disabled it. Enable it again to run it."));
    }

    [Test]
    public void NeedsScopeToEnable_DisabledByARemovedPartition_IsTrue()
    {
        Assert.That(RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "x").NeedsScopeToEnable(), Is.True);
    }

    [Test]
    public void NeedsScopeToEnable_DisabledByHandOrEnabled_IsFalse()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.Manual).NeedsScopeToEnable(), Is.False);
            Assert.That(RunProfile().NeedsScopeToEnable(), Is.False);
        }
    }

    [Test]
    public void ApplyEnabledStateChange_EnablingARemovedPartitionsRunProfileWithNoScope_IsRefusedAndChangesNothing()
    {
        var updated = RunProfile(enabled: true, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(DisabledByRemovedPartition(), updated, allSelectedPartitions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.EqualTo(
                "Run Profile 'Full Import (Glitterband)' was disabled because a hierarchy refresh removed the partition it targeted, " +
                "'dc=glitterband,dc=local'. To enable it, choose the partition it reads, or choose all selected partitions."));
            Assert.That(updated.Partition, Is.Null, "a refusal widens nothing");
        }
    }

    [Test]
    public void ApplyEnabledStateChange_EnablingARemovedPartitionsRunProfileWithAPartition_EnablesItAndClearsTheReason()
    {
        var partition = Partition();
        var updated = RunProfile(enabled: true, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local", partition);

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(DisabledByRemovedPartition(), updated, allSelectedPartitions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Null);
            Assert.That(updated.Enabled, Is.True);
            Assert.That(updated.Partition, Is.SameAs(partition));
            Assert.That(updated.DisabledReason, Is.Null, "an enabled Run Profile has no reason to be disabled");
            Assert.That(updated.DisabledPartitionName, Is.Null);
        }
    }

    [Test]
    public void ApplyEnabledStateChange_EnablingARemovedPartitionsRunProfileForAllSelectedPartitions_EnablesItWithNoPartition()
    {
        var updated = RunProfile(enabled: true, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(DisabledByRemovedPartition(), updated, allSelectedPartitions: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Null, "choosing all selected partitions is an explicit scope");
            Assert.That(updated.Enabled, Is.True);
            Assert.That(updated.Partition, Is.Null);
            Assert.That(updated.DisabledReason, Is.Null);
        }
    }

    [Test]
    public void ApplyEnabledStateChange_AllSelectedPartitions_ClearsAnyPartition()
    {
        var updated = RunProfile(partition: Partition());

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(new ConnectedSystemRunProfileEnabledState(true, null, null), updated, allSelectedPartitions: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Null);
            Assert.That(updated.Partition, Is.Null, "all selected partitions means no single Partition");
        }
    }

    [Test]
    public void ApplyEnabledStateChange_EnablingARunProfileDisabledByHand_NeedsNoScope()
    {
        var updated = RunProfile(enabled: true, ConnectedSystemRunProfileDisabledReason.Manual);

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(
            new ConnectedSystemRunProfileEnabledState(false, ConnectedSystemRunProfileDisabledReason.Manual, null), updated, allSelectedPartitions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Null, "a Run Profile disabled by hand kept its scope, so enabling it widens nothing");
            Assert.That(updated.Enabled, Is.True);
            Assert.That(updated.DisabledReason, Is.Null);
        }
    }

    [Test]
    public void ApplyEnabledStateChange_DisablingAnEnabledRunProfile_RecordsThatAnAdministratorDisabledIt()
    {
        var updated = RunProfile(enabled: false);

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(new ConnectedSystemRunProfileEnabledState(true, null, null), updated, allSelectedPartitions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Null);
            Assert.That(updated.DisabledReason, Is.EqualTo(ConnectedSystemRunProfileDisabledReason.Manual));
            Assert.That(updated.DisabledPartitionName, Is.Null);
        }
    }

    [Test]
    public void ApplyEnabledStateChange_StayingDisabled_KeepsTheStoredReasonWhateverTheCallerSent()
    {
        // The reason is the server's to record, never the caller's: a copy that says otherwise is stale or made up.
        var updated = RunProfile(enabled: false, ConnectedSystemRunProfileDisabledReason.Manual);

        RunProfileEnablement.ApplyEnabledStateChange(DisabledByRemovedPartition(), updated, allSelectedPartitions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.DisabledReason, Is.EqualTo(ConnectedSystemRunProfileDisabledReason.PartitionRemoved));
            Assert.That(updated.DisabledPartitionName, Is.EqualTo("dc=glitterband,dc=local"));
        }
    }

    private static HierarchyRefreshRunProfileItem Affected(string name, string partition) =>
        new() { RunProfileId = 1, RunProfileName = name, PartitionName = partition };

    [Test]
    public void DescribeRunProfilesDisabledByRefresh_OneRunProfile_NamesItItsPartitionAndTheRemedy()
    {
        var message = RunProfileEnablement.DescribeRunProfilesDisabledByRefresh([Affected("Full Import (Glitterband)", "dc=glitterband,dc=local")]);

        Assert.That(message, Is.EqualTo(
            "Disabled 1 Run Profile that targeted a partition this refresh removed: 'Full Import (Glitterband)' (partition 'dc=glitterband,dc=local'). " +
            "Enable each one again, choosing the partition it reads."));
    }

    [Test]
    public void DescribeCancelledRefresh_SeveralRunProfiles_SaysNothingWasSavedAndNamesEach()
    {
        var message = RunProfileEnablement.DescribeCancelledRefresh(
        [
            Affected("Full Import (Glitterband)", "dc=glitterband,dc=local"),
            Affected("Delta Import (Glitterband)", "dc=glitterband,dc=local")
        ]);

        Assert.That(message, Is.EqualTo(
            "Nothing from this refresh was saved. It would remove a partition that 2 Run Profiles target: " +
            "'Full Import (Glitterband)' (partition 'dc=glitterband,dc=local'), 'Delta Import (Glitterband)' (partition 'dc=glitterband,dc=local'). " +
            "Refresh again choosing to disable those Run Profiles, or point them at another partition first."));
    }

    [Test]
    public void ApplyEnabledStateChange_NoStoredState_KeepsTheCallersStateAndRecordsADisabledRunProfileAsDisabledByHand()
    {
        var updated = RunProfile(enabled: false);

        var refusal = RunProfileEnablement.ApplyEnabledStateChange(stored: null, updated, allSelectedPartitions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Null);
            Assert.That(updated.Enabled, Is.False);
            Assert.That(updated.DisabledReason, Is.EqualTo(ConnectedSystemRunProfileDisabledReason.Manual));
        }
    }
}
