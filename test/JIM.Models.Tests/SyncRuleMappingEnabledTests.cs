// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests;

/// <summary>
/// An Attribute Flow mapping can be disabled (#1485): the schema refresh decision's "Apply and Disable
/// Dependents" option needs a per-mapping state to actuate, and an administrator needs to disable one flow
/// without touching the whole Synchronisation Rule. These tests pin the model contract.
/// </summary>
[TestFixture]
public class SyncRuleMappingEnabledTests
{
    [Test]
    public void SyncRuleMapping_ByDefault_IsEnabled()
    {
        // Every mapping persisted before this field existed must behave exactly as it always has, so the
        // default is enabled; the store default in the migration backfills existing rows the same way.
        var mapping = new SyncRuleMapping();

        Assert.That(mapping.Enabled, Is.True);
    }

    [Test]
    public void SyncRuleMapping_DisabledReason_DefaultsToNull()
    {
        // The reason is only ever set when something (the schema refresh decision, later) disables the
        // mapping on the administrator's behalf; an administrator's own disable carries no reason.
        var mapping = new SyncRuleMapping();

        Assert.That(mapping.DisabledReason, Is.Null);
    }
}
