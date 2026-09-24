// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// <see cref="SyncRuleAttributeFlowProposalMaterialiser"/>'s Unique Value Generation wiring (#242, Phase 3): a
/// proposed generated mapping's settings must be attached to the stand-in mapping, so
/// <see cref="SyncRuleMapping.GetSourceType"/> sees <see cref="SyncRuleMappingSourcesType.GeneratedMapping"/> for
/// an unsaved generated mapping exactly as it would for a saved one, letting Sync Preview evaluate it as the
/// real engine would.
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowProposalMaterialiserGenerationTests
{
    private const int AttributeId = 501;

    [Test]
    public void Materialise_GeneratedMappingProposal_AttachesTheGenerationSettingsAndSourceTypeReportsGenerated()
    {
        var storedRule = BuildImportRule();
        var generation = new SyncRuleMappingGenerationProposal(
            GeneratedValueTokenKind.Sequence, GeneratedValueSuffixStyle.Number, 1, 1000, 1, null,
            GeneratedValueWidthOverflowBehaviour.StopAndReport, GeneratedValueRandomFormat.Guid, null, null, 1000, true);
        var proposal = new SyncRuleAttributeFlowProposal([new SyncRuleMappingProposal(AttributeId, null, [], Generation: generation)]);

        var standIn = SyncRuleAttributeFlowProposalMaterialiser.Materialise(storedRule, proposal, [], MetaverseAttributes());

        var mapping = standIn.AttributeFlowRules.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.Generation, Is.Not.Null);
            Assert.That(mapping.Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence));
            Assert.That(mapping.Generation.SequenceStart, Is.EqualTo(1000));
            Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.GeneratedMapping));
        }
    }

    [Test]
    public void Materialise_OrdinaryMappingProposal_LeavesGenerationNull()
    {
        var storedRule = BuildImportRule();
        var proposal = new SyncRuleAttributeFlowProposal([
            new SyncRuleMappingProposal(AttributeId, null, [new SyncRuleMappingSourceProposal(0, null, null, "\"fixed\"")])
        ]);

        var standIn = SyncRuleAttributeFlowProposalMaterialiser.Materialise(storedRule, proposal, [], MetaverseAttributes());

        var mapping = standIn.AttributeFlowRules.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.Generation, Is.Null);
            Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.ExpressionMapping));
        }
    }

    private static SyncRule BuildImportRule() => new()
    {
        Id = 1,
        Direction = SyncRuleDirection.Import,
        ConnectedSystem = new ConnectedSystem { Id = 1, Name = "System" }
    };

    private static List<MetaverseAttribute> MetaverseAttributes() =>
        [new MetaverseAttribute { Id = AttributeId, Name = "Employee Number", Type = AttributeDataType.LongNumber }];
}
