// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Generated Value Forms in Sync Preview (#2023): the preview checks the values built from a generated value with each
/// candidate, as the run does, against JIM's own records (it never probes), so the value it says would be generated is
/// the one the run generates; it says when the value would wait for an input, and which Connected Systems the run would
/// probe for each built value.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    private const string FormsEmailFromAccountNameAndRegion = "mv[\"Account Name\"] + \"@\" + Lower(mv[\"Region\"]) + \".corp.local\"";
    private const string FormsAccountNameFromEmployeeId = "Lower(mv[\"EmployeeId\"])";

    [Test]
    public async Task PreviewSyncForCsoAsync_EmailBuiltFromTheFirstCandidateIsHeld_PreviewPassesItOverAsTheRealRunDoesAsync()
    {
        var ctx = await SetUpDerivedAsync(generatedAccountNameBase: FormsAccountNameFromEmployeeId);
        SeedFormsMetaverseObjectHolding(ctx.Email, "e1@corp.local");
        var cso = SeedDerivedHr(ctx, "E1", accountName: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single(m => DerivedText(m, ctx.AccountName) != null);
        var skipped = Flatten(preview.OutcomeTree)
            .Where(node => node.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueCandidatesSkipped)
            .Select(node => node.DetailMessage)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors, Is.Empty);
            Assert.That(PreviewedValue(preview, "Account Name"), Is.Not.EqualTo("e1"), "e1's Email is already held, so the preview passes it over");
            Assert.That(DerivedText(mvo, ctx.AccountName), Is.EqualTo(PreviewedValue(preview, "Account Name")), "the real run generated what the preview said");
            Assert.That(skipped, Has.Count.EqualTo(1));
            Assert.That(skipped[0], Does.StartWith("Account Name: e1: Email (built from Account Name)"), "the preview names the value that held the candidate back");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the passed-over candidate sits where the real run records it");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_EmailNeedsARegionTheObjectHasNot_PreviewSaysTheAccountNameWouldWaitAsync()
    {
        var ctx = await SetUpDerivedAsync(emailExpression: FormsEmailFromAccountNameAndRegion, withUpn: false, withAd: true,
            generatedAccountNameBase: FormsAccountNameFromEmployeeId);
        var cso = SeedDerivedHr(ctx, "E1", accountName: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        var waiting = preview.Warnings.Where(w => w.Code == SyncPreviewMessageCode.GeneratedValueWaiting).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors, Is.Empty, "waiting is not an error");
            Assert.That(PreviewedValue(preview, "Account Name"), Is.Null, "no Account Name until the Email built from it can be worked out");
            Assert.That(waiting, Has.Count.EqualTo(1));
            Assert.That(waiting[0].AttributeName, Is.EqualTo("Account Name"));
            Assert.That(waiting[0].Detail, Does.Contain("Region (needed by Email)"), "the preview names the input and the value that needs it");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the wait sits where the real run records it");
            Assert.That(DescribeTree(preview.OutcomeTree), Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueWaiting)));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_EmailWrittenToAProbingConnector_RecordsTheProbeForTheEmailAsync()
    {
        var ctx = await SetUpDerivedAsync(withUpn: false, generatedAccountNameBase: FormsAccountNameFromEmployeeId);
        await AddFormsDirectoryExportAsync(ctx.Email);
        var cso = SeedDerivedHr(ctx, "E1", accountName: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);

        var probe = preview.GeneratedValueProbes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.AttributeName, Is.EqualTo("Email"));
            Assert.That(probe.Value, Is.EqualTo("e1@corp.local"), "the Email built from the generated Account Name");
            Assert.That(probe.BuiltFromAttributeName, Is.EqualTo("Account Name"));
            Assert.That(probe.ConnectedSystemNames, Is.EqualTo(new[] { "Corp Directory" }));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ProbingSystemExcludedFromTheGeneratedValue_RecordsNoProbeForTheEmailAsync()
    {
        // Plan decision 2: per-system Exclusions on the generated value apply to every value built from it bound there.
        var ctx = await SetUpDerivedAsync(withUpn: false, generatedAccountNameBase: FormsAccountNameFromEmployeeId);
        var directoryId = await AddFormsDirectoryExportAsync(ctx.Email);
        var generated = DbContext.SyncRuleMappings.Single(m => m.Generation != null);
        generated.Generation!.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = directoryId });
        await DbContext.SaveChangesAsync();
        var cso = SeedDerivedHr(ctx, "E1", accountName: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);

        Assert.That(preview.GeneratedValueProbes, Is.Empty);
    }

    /// <summary>
    /// An export rule sending Email unchanged to mail in a Connected System whose Connector probes.
    /// </summary>
    private async Task<int> AddFormsDirectoryExportAsync(MetaverseAttribute email)
    {
        var mvType = email.MetaverseObjectTypes.First();
        var directory = await CreateConnectedSystemAsync("Corp Directory");
        directory.ConnectorDefinition.Name = ConnectorConstants.LdapConnectorName;
        directory.ConnectorDefinition.SupportsUniquenessProbe = true;
        var directoryType = await CreateCsoTypeAsync(directory.Id, "user", [
            new ConnectedSystemObjectTypeAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "mail", Type = AttributeDataType.Text, Selected = true }
        ]);
        var export = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Corp Directory Export");
        FlowToAttribute(export, directoryType.Attributes.Single(a => a.Name == "mail"), email);
        await DbContext.SaveChangesAsync();
        return directory.Id;
    }

    private void SeedFormsMetaverseObjectHolding(MetaverseAttribute attribute, string value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow, Type = attribute.MetaverseObjectTypes.First() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = value, MetaverseObject = mvo });
        SyncRepo.SeedMetaverseObject(mvo);
    }
}
