// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests;

/// <summary>
/// Which attribute a mapping targets, read before EF has fixed the foreign key up from the navigation. The portal's
/// Attribute Flow editor binds the target to the navigation on a tracked rule, so between a retarget and SaveChanges
/// the scalar still names the old target; every pre-write reader must take the navigation when it is loaded (#1750).
/// </summary>
[TestFixture]
public class SyncRuleMappingTargetResolutionTests
{
    private static readonly MetaverseAttribute AccountName = new() { Id = 10, Name = "Account Name", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Email = new() { Id = 11, Name = "Email", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute Mail = new() { Id = 20, Name = "mail", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute UserPrincipalName = new() { Id = 21, Name = "userPrincipalName", Type = AttributeDataType.Text };

    [Test]
    public void ResolveTargetMetaverseAttributeId_NavigationRetargetedScalarStale_ReturnsTheNavigation()
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttributeId = AccountName.Id, TargetMetaverseAttribute = Email };

        Assert.That(mapping.ResolveTargetMetaverseAttributeId(), Is.EqualTo(Email.Id));
    }

    [Test]
    public void ResolveTargetMetaverseAttributeId_NavigationNotLoaded_ReturnsTheScalar()
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttributeId = AccountName.Id };

        Assert.That(mapping.ResolveTargetMetaverseAttributeId(), Is.EqualTo(AccountName.Id));
    }

    [Test]
    public void ResolveTargetMetaverseAttributeId_NoTarget_ReturnsNull()
    {
        Assert.That(new SyncRuleMapping().ResolveTargetMetaverseAttributeId(), Is.Null);
    }

    [Test]
    public void ResolveTargetConnectedSystemAttributeId_NavigationRetargetedScalarStale_ReturnsTheNavigation()
    {
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttributeId = Mail.Id, TargetConnectedSystemAttribute = UserPrincipalName };

        Assert.That(mapping.ResolveTargetConnectedSystemAttributeId(), Is.EqualTo(UserPrincipalName.Id));
    }

    [Test]
    public void ResolveMetaverseObjectTypeId_UnsavedRuleWithTheTypeOnTheNavigation_ReturnsTheNavigation()
    {
        var rule = new SyncRule { MetaverseObjectType = new MetaverseObjectType { Id = 3, Name = "User" } };

        Assert.That(rule.ResolveMetaverseObjectTypeId(), Is.EqualTo(3));
    }

    [Test]
    public void ResolveMetaverseObjectTypeId_NavigationNotLoaded_ReturnsTheScalar()
    {
        var rule = new SyncRule { MetaverseObjectTypeId = 3 };

        Assert.That(rule.ResolveMetaverseObjectTypeId(), Is.EqualTo(3));
    }

    [Test]
    public void FromMapping_ImportMappingRetargetedInTheEditor_ProposesTheChosenTarget()
    {
        // The Configuration Change Preview is built from the editor's staged rule: previewing a retarget must
        // describe the new target, not the one the stale scalar still names.
        var mapping = new SyncRuleMapping { TargetMetaverseAttributeId = AccountName.Id, TargetMetaverseAttribute = Email };

        Assert.That(SyncRuleMappingProposal.FromMapping(mapping).TargetMetaverseAttributeId, Is.EqualTo(Email.Id));
    }

    [Test]
    public void FromMapping_ExportMappingRetargetedInTheEditor_ProposesTheChosenTarget()
    {
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttributeId = Mail.Id, TargetConnectedSystemAttribute = UserPrincipalName };

        Assert.That(SyncRuleMappingProposal.FromMapping(mapping).TargetConnectedSystemAttributeId, Is.EqualTo(UserPrincipalName.Id));
    }
}
