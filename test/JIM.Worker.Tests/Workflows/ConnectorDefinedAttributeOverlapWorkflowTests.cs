// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Connector-defined Attribute can declare the Connected System attribute it is realised through (#2756): the LDAP
/// Connector's Exchange Alias is written to the directory as <c>mailNickname</c>. An export Synchronisation Rule that
/// flows both, from different sources, leaves each export free to overwrite the other's value, so saving it warns. The
/// save always goes ahead: two different sources can still agree, and flowing the underlying attribute on its own is
/// ordinary. The check is generic and knows nothing about any particular Connector.
/// </summary>
[TestFixture]
public class ConnectorDefinedAttributeOverlapWorkflowTests : WorkflowTestBase
{
    private const string RuleName = "AD Accounts";

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_FlowsTheAttributeAndItsSourceFromDifferentSources_WarnsNamingBothAsync()
    {
        var arrangement = await ArrangeAsync();
        AddExportFlow(arrangement, "mailNickname", arrangement.MvEmployeeId);
        AddExportFlow(arrangement, "Exchange Alias", arrangement.MvDisplayName);
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync();

        var warnings = OverlapWarnings(rule);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings.Single(), Does.Contain("'Exchange Alias'").And.Contain("'mailNickname'").And.Contain("JIM Test Connector"));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_BothFlowFromTheSameSource_DoesNotWarnAsync()
    {
        var arrangement = await ArrangeAsync();
        AddExportFlow(arrangement, "mailNickname", arrangement.MvDisplayName);
        AddExportFlow(arrangement, "Exchange Alias", arrangement.MvDisplayName);
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync();

        Assert.That(OverlapWarnings(rule), Is.Empty, "both write the same value, so neither can overwrite the other");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_OnlyTheUnderlyingAttributeIsFlowed_DoesNotWarnAsync()
    {
        var arrangement = await ArrangeAsync();
        AddExportFlow(arrangement, "mailNickname", arrangement.MvEmployeeId);
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync();

        Assert.That(OverlapWarnings(rule), Is.Empty, "managing the underlying attribute on its own is ordinary");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_OneOfTheFlowsIsDisabled_DoesNotWarnAsync()
    {
        var arrangement = await ArrangeAsync();
        AddExportFlow(arrangement, "mailNickname", arrangement.MvEmployeeId).Enabled = false;
        AddExportFlow(arrangement, "Exchange Alias", arrangement.MvDisplayName);
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync();

        Assert.That(OverlapWarnings(rule), Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ConnectorDeclaresNoSource_DoesNotWarnAsync()
    {
        var arrangement = await ArrangeAsync(declaredSource: null);
        AddExportFlow(arrangement, "mailNickname", arrangement.MvEmployeeId);
        AddExportFlow(arrangement, "Exchange Alias", arrangement.MvDisplayName);
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync();

        Assert.That(OverlapWarnings(rule), Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_TheRuleIsDisabled_StillWarnsAsync()
    {
        var arrangement = await ArrangeAsync();
        AddExportFlow(arrangement, "mailNickname", arrangement.MvEmployeeId);
        AddExportFlow(arrangement, "Exchange Alias", arrangement.MvDisplayName);
        arrangement.Rule.Enabled = false;
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync();

        Assert.That(OverlapWarnings(rule), Has.Count.EqualTo(1), "enabling the rule later must not hide the problem");
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_NewFlowOverlapsAnExistingOne_WarnsOnTheMappingAsync()
    {
        var arrangement = await ArrangeAsync();
        AddExportFlow(arrangement, "mailNickname", arrangement.MvEmployeeId);
        await DbContext.SaveChangesAsync();
        var target = arrangement.UserType.Attributes.Single(a => a.Name == "Exchange Alias");
        var rule = await Jim.ConnectedSystems.GetSyncRuleAsync(arrangement.Rule.Id) ?? throw new InvalidOperationException("arrange: the rule loads");
        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = arrangement.MvDisplayName, MetaverseAttributeId = arrangement.MvDisplayName.Id } }
        };

        await Jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, await AdministratorAsync());

        Assert.That(mapping.SaveWarnings.Where(w => w.Contains("'Exchange Alias'") && w.Contains("'mailNickname'")).ToList(), Has.Count.EqualTo(1));
    }

    private static List<string> OverlapWarnings(SyncRule rule) =>
        rule.SaveWarnings.Where(w => w.Contains("Exchange Alias")).ToList();

    private sealed record Arrangement(SyncRule Rule, ConnectedSystemObjectType UserType, MetaverseAttribute MvDisplayName, MetaverseAttribute MvEmployeeId);

    /// <summary>
    /// A directory whose "user" Object Type carries <c>mailNickname</c> and a Connector-defined Exchange Alias declaring
    /// it as its source, and an empty export rule for it.
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(string? declaredSource = "mailNickname")
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var directory = await CreateConnectedSystemAsync("AD");
        var userType = await CreateCsoTypeAsync(directory.Id, "user",
        [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "mailNickname", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute
            {
                Name = "Exchange Alias", Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true, SourceAttributeName = declaredSource
            }
        ]);
        var rule = await CreateExportSyncRuleAsync(directory.Id, userType, mvType, RuleName);
        return new Arrangement(rule, userType, mvType.Attributes.First(a => a.Name == "DisplayName"), mvType.Attributes.First(a => a.Name == "EmployeeId"));
    }

    private static SyncRuleMapping AddExportFlow(Arrangement arrangement, string csAttributeName, MetaverseAttribute source)
    {
        var target = arrangement.UserType.Attributes.Single(a => a.Name == csAttributeName);
        var mapping = new SyncRuleMapping
        {
            SyncRule = arrangement.Rule,
            SyncRuleId = arrangement.Rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        };
        arrangement.Rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private async Task<MetaverseObject> AdministratorAsync()
    {
        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();
        return administrator;
    }

    /// <summary>
    /// Saves the rule through the one write path the portal, the REST API and PowerShell share, and returns the instance
    /// the save stamped its warnings on.
    /// </summary>
    private async Task<SyncRule> SavedRuleAsync()
    {
        var administrator = await AdministratorAsync();
        var rule = await Jim.ConnectedSystems.GetSyncRuleAsync(DbContext.SyncRules.Single(r => r.Name == RuleName).Id)
            ?? throw new InvalidOperationException("arrange: the rule loads");
        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, administrator), Is.True, "the save succeeds: a warning, not a refusal");
        return rule;
    }
}
