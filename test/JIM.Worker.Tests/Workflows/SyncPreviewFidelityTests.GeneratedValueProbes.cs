// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Sync Preview and probing (Unique Value Generation, #242, release 3): the preview is a dry run, so it never probes,
/// and says so. For each value it generates that the real synchronisation would probe for, it records the Connected
/// Systems that would be probed, computed from the preview's own export rules.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    private async Task<(ConnectedSystem Hr, Guid CsoId, MetaverseAttribute AccountName, MetaverseObjectType MvType, SyncRuleMapping Generated)> SetUpGeneratedAccountNameAsync()
    {
        var hr = await CreateConnectedSystemAsync("HR Source");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        var accountName = new MetaverseAttribute
        {
            Name = "Account Name",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(accountName);
        mvType.Attributes.Add(accountName);
        await DbContext.SaveChangesAsync();

        var importRule = await CreateImportSyncRuleWithDisplayNameFlowAsync(hr, hrType, mvType);
        var generated = new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = accountName,
            TargetMetaverseAttributeId = accountName.Id,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken, AttemptLimit = 100 },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"EmployeeId\"])" } }
        };
        importRule.AttributeFlowRules.Add(generated);
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(hr.Id, hrType, "John Smith", "EMP001");
        return (hr, cso.Id, accountName, mvType, generated);
    }

    /// <summary>
    /// An export rule sending Account Name unchanged to a Connected System whose Connector Definition is
    /// <paramref name="connectorName"/>, declaring the probe as <paramref name="supportsProbe"/> says.
    /// </summary>
    private async Task AddDirectoryExportAsync(string systemName, string connectorName, bool supportsProbe, MetaverseAttribute accountName, MetaverseObjectType mvType)
    {
        var directory = await CreateConnectedSystemAsync(systemName);
        directory.ConnectorDefinition.Name = connectorName;
        directory.ConnectorDefinition.SupportsUniquenessProbe = supportsProbe;
        var directoryType = await CreateCsoTypeAsync(directory.Id, "user", [
            new ConnectedSystemObjectTypeAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "sAMAccountName", Type = AttributeDataType.Text, Selected = true }
        ]);
        var exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, $"{systemName} Export");
        var target = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = accountName, MetaverseAttributeId = accountName.Id } }
        });
        await DbContext.SaveChangesAsync();
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_GeneratedValueExportedToAProbingConnector_RecordsTheSystemTheRunWouldProbeAsync()
    {
        var setup = await SetUpGeneratedAccountNameAsync();
        await AddDirectoryExportAsync("Corp Directory", ConnectorConstants.LdapConnectorName, supportsProbe: true, setup.AccountName, setup.MvType);
        await AddDirectoryExportAsync("Payroll", "JIM Test Connector", supportsProbe: false, setup.AccountName, setup.MvType);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(setup.Hr.Id, setup.CsoId);

        var probe = preview.GeneratedValueProbes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.AttributeName, Is.EqualTo("Account Name"));
            Assert.That(probe.Value, Is.EqualTo("emp001"));
            Assert.That(probe.ConnectedSystemNames, Is.EqualTo(new[] { "Corp Directory" }),
                "only the system whose Connector probes is named; Payroll is checked against JIM's records alone, as in the preview");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ProbingSystemExcluded_RecordsNoProbeAsync()
    {
        var setup = await SetUpGeneratedAccountNameAsync();
        await AddDirectoryExportAsync("Corp Directory", ConnectorConstants.LdapConnectorName, supportsProbe: true, setup.AccountName, setup.MvType);
        var directoryId = DbContext.ConnectedSystems.Single(cs => cs.Name == "Corp Directory").Id;
        setup.Generated.Generation!.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = directoryId });
        await DbContext.SaveChangesAsync();

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(setup.Hr.Id, setup.CsoId);

        Assert.That(preview.GeneratedValueProbes, Is.Empty);
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_NoProbingTarget_RecordsNoProbeAsync()
    {
        var setup = await SetUpGeneratedAccountNameAsync();
        await AddDirectoryExportAsync("Payroll", "JIM Test Connector", supportsProbe: false, setup.AccountName, setup.MvType);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(setup.Hr.Id, setup.CsoId);

        Assert.That(preview.GeneratedValueProbes, Is.Empty);
    }
}
