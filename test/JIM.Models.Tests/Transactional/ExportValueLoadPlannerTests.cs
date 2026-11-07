// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// Which of a batch's object attributes export execution may load in part (#2816): only a multi-valued reference
/// attribute that identifies and names nothing, and only where every change the batch makes to it can be put as a
/// question the database answers exactly. Anything else, or anything unsure, is loaded whole, which is always correct.
/// </summary>
[TestFixture]
public class ExportValueLoadPlannerTests
{
    private const int GroupTypeId = 7;

    private static ConnectedSystemObjectTypeAttribute Attribute(int id, string name, AttributeDataType type = AttributeDataType.Reference,
        AttributePlurality plurality = AttributePlurality.MultiValued, bool caseExact = false) =>
        new() { Id = id, Name = name, Type = type, AttributePlurality = plurality, CaseExact = caseExact };

    private static readonly ConnectedSystemObjectTypeAttribute Member = Attribute(2, "member");

    private static PendingExport Export(ConnectedSystemObject group, params PendingExportAttributeValueChange[] changes)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update, ConnectedSystemObject = group, ConnectedSystemObjectId = group.Id
        };
        export.AttributeValueChanges.AddRange(changes);
        return export;
    }

    private static ConnectedSystemObject Group() => new() { Id = Guid.NewGuid(), TypeId = GroupTypeId };

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectTypeAttribute attribute, PendingExportAttributeChangeType changeType,
        string? stringValue = null, Guid? objectId = null, string? unresolved = null) => new()
    {
        Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, ChangeType = changeType, StringValue = stringValue,
        ResolvedReferenceCsoId = objectId, UnresolvedReferenceValue = unresolved
    };

    private static List<ExportValueTrimCandidate> Plan(IEnumerable<PendingExport> exports, bool databaseFoldsCase = true,
        params ConnectedSystemObjectTypeAttribute[] attributes) =>
        ExportValueLoadPlanner.PlanCandidates(exports,
            new Dictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>> { [GroupTypeId] = attributes.Length > 0 ? attributes : [Member] },
            databaseFoldsCase);

    [Test]
    public void PlanCandidates_RemovalsNamingObjects_AsksForTheirRowsWithTheCapAboveTheChangeCount()
    {
        var group = Group();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var candidates = Plan([Export(group,
            Change(Member, PendingExportAttributeChangeType.Remove, "cn=user1,ou=people", first),
            Change(Member, PendingExportAttributeChangeType.Remove, "cn=user2,ou=people", second))]);

        var candidate = candidates.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate.ConnectedSystemObjectId, Is.EqualTo(group.Id));
            Assert.That(candidate.Attribute.Id, Is.EqualTo(Member.Id));
            Assert.That(candidate.FallbackReason, Is.Null);
            Assert.That(candidate.Cap, Is.EqualTo(4), "two changes, plus two");
            Assert.That(candidate.Question!.ReferenceIds, Is.EquivalentTo(new[] { first, second }));
            Assert.That(candidate.Question.CaseInsensitiveValues, Is.EquivalentTo(new[] { "cn=user1,ou=people", "cn=user2,ou=people" }));
            Assert.That(candidate.Question.ExactValues, Is.Empty);
            Assert.That(candidate.Question.NeedsCount, Is.False);
        }
    }

    [Test]
    public void PlanCandidates_TwoExportsForOneObject_PlanTheAttributeOnceOverBothExportsChanges()
    {
        var group = Group();
        var candidates = Plan([
            Export(group, Change(Member, PendingExportAttributeChangeType.Add, "cn=a,ou=people", Guid.NewGuid())),
            Export(group, Change(Member, PendingExportAttributeChangeType.Remove, "cn=b,ou=people", Guid.NewGuid()))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].Cap, Is.EqualTo(4));
            Assert.That(candidates[0].Question!.ChangeIds, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void PlanCandidates_AttributeNoChangeNames_IsTrimmableWithNothingToAsk()
    {
        var mail = Attribute(3, "mail", AttributeDataType.Text);
        var candidates = Plan([Export(Group(), Change(mail, PendingExportAttributeChangeType.Update, "a@b"))], attributes: [Member, mail]);

        var candidate = candidates.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate.Attribute.Id, Is.EqualTo(Member.Id), "only the multi-valued reference attribute is a candidate");
            Assert.That(candidate.FallbackReason, Is.Null);
            Assert.That(candidate.Cap, Is.EqualTo(2));
            Assert.That(candidate.Question!.TupleCount, Is.Zero);
        }
    }

    [TestCase(PendingExportAttributeChangeType.Update)]
    [TestCase(PendingExportAttributeChangeType.RemoveAll)]
    public void PlanCandidates_ChangeSettingTheWholeSet_LoadsTheAttributeWhole(PendingExportAttributeChangeType changeType)
    {
        var candidates = Plan([Export(Group(),
            Change(Member, PendingExportAttributeChangeType.Remove, "cn=a,ou=people", Guid.NewGuid()),
            Change(Member, changeType, changeType == PendingExportAttributeChangeType.Update ? "cn=b,ou=people" : null, Guid.NewGuid()))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.Single().FallbackReason, Is.EqualTo(ExportValueLoadFallbackReason.WholeSetChange));
            Assert.That(candidates.Single().Question, Is.Null);
        }
    }

    [Test]
    public void PlanCandidates_ReferenceStillAwaitingResolution_LoadsTheAttributeWhole()
    {
        // The deferred pass resolves it after loading, so what it names could change between the load and its use.
        var candidates = Plan([Export(Group(), Change(Member, PendingExportAttributeChangeType.Add, unresolved: Guid.NewGuid().ToString()))]);

        Assert.That(candidates.Single().FallbackReason, Is.EqualTo(ExportValueLoadFallbackReason.UnresolvedReference));
    }

    [Test]
    public void PlanCandidates_CaseInsensitiveStringNamingNoObject_LoadsTheAttributeWhole()
    {
        var candidates = Plan([Export(Group(), Change(Member, PendingExportAttributeChangeType.Remove, "cn=a,ou=people"))]);

        Assert.That(candidates.Single().FallbackReason, Is.EqualTo(ExportValueLoadFallbackReason.NamesNoObject));
    }

    [Test]
    public void PlanCandidates_CaseExactStringNamingNoObject_AsksForItExactly()
    {
        var exactMember = Attribute(2, "member", caseExact: true);
        var candidates = Plan([Export(Group(), Change(exactMember, PendingExportAttributeChangeType.Remove, "cn=a,ou=people"))],
            attributes: [exactMember]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.Single().FallbackReason, Is.Null);
            Assert.That(candidates.Single().Question!.ExactValues, Is.EquivalentTo(new[] { "cn=a,ou=people" }));
        }
    }

    [Test]
    public void PlanCandidates_NonAsciiStringIgnoringCase_LoadsTheAttributeWhole()
    {
        var candidates = Plan([Export(Group(), Change(Member, PendingExportAttributeChangeType.Remove, "cn=Zoë,ou=people", Guid.NewGuid()))]);

        Assert.That(candidates.Single().FallbackReason, Is.EqualTo(ExportValueLoadFallbackReason.NonAsciiValue));
    }

    [Test]
    public void PlanCandidates_DatabaseFoldsCaseDifferently_LoadsTheAttributeWhole()
    {
        var candidates = Plan([Export(Group(), Change(Member, PendingExportAttributeChangeType.Remove, "cn=a,ou=people", Guid.NewGuid()))],
            databaseFoldsCase: false);

        Assert.That(candidates.Single().FallbackReason, Is.EqualTo(ExportValueLoadFallbackReason.DatabaseFoldsCaseDifferently));
    }

    [Test]
    public void PlanCandidates_IdentifyingNamingSingleValuedOrConnectorDefinedAttributes_AreNeverCandidates()
    {
        var externalId = Attribute(10, "entryDN"); externalId.IsExternalId = true;
        var secondary = Attribute(11, "seeAlso"); secondary.IsSecondaryExternalId = true;
        var naming = Attribute(12, "cn");
        var manager = Attribute(13, "manager", plurality: AttributePlurality.SingleValued);
        var connectorDefined = Attribute(14, "proxies"); connectorDefined.IsConnectorDefined = true;
        var text = Attribute(15, "description", AttributeDataType.Text);

        var candidates = Plan([Export(Group())], attributes: [externalId, secondary, naming, manager, connectorDefined, text]);

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public void PlanCandidates_ExportWithoutAnObject_PlansNothing()
    {
        var export = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Create };

        Assert.That(Plan([export]), Is.Empty);
    }

    [Test]
    public void BuildValueLoads_HeldAtOrAboveTheCap_IsLoadedInPartAndBelowIt_Whole()
    {
        var big = Group();
        var small = Group();
        var fellBack = Group();
        var candidates = Plan([
            Export(big, Change(Member, PendingExportAttributeChangeType.Remove, "cn=a,ou=people", Guid.NewGuid())),
            Export(small, Change(Member, PendingExportAttributeChangeType.Remove, "cn=a,ou=people", Guid.NewGuid())),
            Export(fellBack, Change(Member, PendingExportAttributeChangeType.RemoveAll))]);
        var counts = new Dictionary<(Guid, int), int>
        {
            [(big.Id, Member.Id)] = 3,
            [(small.Id, Member.Id)] = 2,
            [(fellBack.Id, Member.Id)] = 3
        };

        var loads = ExportValueLoadPlanner.BuildValueLoads(candidates, counts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loads[big.Id].PartiallyLoadedAttributes[Member.Id].AtLeastValueCount, Is.EqualTo(3));
            Assert.That(loads[big.Id].PartiallyLoadedAttributes[Member.Id].AttributeName, Is.EqualTo("member"));
            Assert.That(loads.ContainsKey(small.Id), Is.False, "two held, below the cap of three: loaded whole, as ever");
            Assert.That(loads[fellBack.Id].PartiallyLoadedAttributes, Is.Empty);
            Assert.That(loads[fellBack.Id].LoadedWholeAttributes[Member.Id], Is.EqualTo(ExportValueLoadFallbackReason.WholeSetChange),
                "a large attribute loaded whole says why");
        }
    }

    [Test]
    public void BuildValueLoads_CandidateWithoutACount_Throws()
    {
        var candidates = Plan([Export(Group(), Change(Member, PendingExportAttributeChangeType.Remove, "cn=a,ou=people", Guid.NewGuid()))]);

        Assert.That(() => ExportValueLoadPlanner.BuildValueLoads(candidates, new Dictionary<(Guid, int), int>()),
            Throws.InvalidOperationException);
    }
}
