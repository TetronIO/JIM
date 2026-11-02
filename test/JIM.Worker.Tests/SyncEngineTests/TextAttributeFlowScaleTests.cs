// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Scale guard for diffing a large multi-valued Text attribute in inbound Attribute Flow (#2666), the synchronisation
/// side of the import diff #988 fixed. The writer asked, of each value the Metaverse Object holds, whether the source
/// holds it, and of each source value whether the Metaverse Object holds it, each by scanning the other side, so every
/// synchronisation of a large attribute cost its size times the source's even when nothing had changed. The tests drive
/// the public inbound flow, so the bound covers everything the writer's caller does to the values on the way.
/// </summary>
[TestFixture]
public class TextAttributeFlowScaleTests
{
    private const int MvoTypeId = 10;
    private const int MvoAttributeId = 100;
    private const int CsoAttributeId = 200;
    private const int ConnectedSystemId = 5;
    private const int SyncRuleId = 7;
    private const int ValueCount = 50_000;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    /// <summary>
    /// A person holding 50,000 Mail Aliases whose source now holds the same aliases bar one, replaced by a new one: the
    /// everyday shape of a large attribute's synchronisation, a handful changed among many unchanged. Only the replaced
    /// alias is removed and only the new one added. Measured when #2666 was fixed, this took about 26 s before the fix
    /// and about 45 ms after; the bound sits well clear of both, so a return of the per-value scan fails loudly rather
    /// than flakily.
    /// </summary>
    [Test]
    public void FlowInboundAttributes_FiftyThousandTextValuesWithOneReplaced_StagesOnlyTheChangeWithinBound()
    {
        var mvoAttribute = new MetaverseAttribute
        {
            Id = MvoAttributeId, Name = "Mail Aliases", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
        };
        var csoAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsoAttributeId, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttribute] };
        var mapping = new SyncRuleMapping { Id = SyncRuleId, SyncRuleId = SyncRuleId, TargetMetaverseAttribute = mvoAttribute, Priority = 1 };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = CsoAttributeId, ConnectedSystemAttribute = csoAttribute, Order = 1 });
        var rule = new SyncRule
        {
            Id = SyncRuleId, MetaverseObjectTypeId = MvoTypeId, Direction = SyncRuleDirection.Import, Enabled = true, AttributeFlowRules = [mapping]
        };

        var person = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = csoType.Id, ConnectedSystemId = ConnectedSystemId, MetaverseObject = person };
        for (var i = 0; i < ValueCount; i++)
        {
            person.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), MetaverseObject = person, Attribute = mvoAttribute, AttributeId = MvoAttributeId, StringValue = Alias(i),
                ContributedBySystemId = ConnectedSystemId, ContributedBySyncRuleId = SyncRuleId
            });
        }

        // The source holds every alias but the first, which a new alias replaces.
        for (var i = 1; i <= ValueCount; i++)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsoAttributeId, Attribute = csoAttribute, ConnectedSystemObject = cso, StringValue = Alias(i) });
        var replaced = person.AttributeValues[0];

        var stopwatch = Stopwatch.StartNew();
        var errors = _engine.FlowInboundAttributes(cso, rule, [csoType]);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(person.PendingAttributeValueRemovals, Is.EqualTo(new[] { replaced }), "only the replaced alias is removed");
            Assert.That(person.PendingAttributeValueAdditions.Select(av => av.StringValue), Is.EqualTo(new[] { Alias(ValueCount) }),
                "only the new alias is added");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"synchronising a {ValueCount:N0}-value Text attribute with one value replaced took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "each side's values must be looked up in a set of the other's, not found by a scan of them");
        }
    }

    private static string Alias(int i) => $"smtp:alias-{i:D6}@example.com";
}
