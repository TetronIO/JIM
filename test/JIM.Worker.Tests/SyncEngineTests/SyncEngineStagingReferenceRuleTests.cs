// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Export staging's no-net-change check answers whether a Connected System Object already holds a reference by the rule
/// reconciliation and the recall query use (#2803): a resolved row matches a change naming an object by that object
/// alone, never by its distinguished name; an unresolved row matches by its distinguished name, ignoring case unless the
/// attribute is case-exact; a change naming no object matches any row by its distinguished name. The scan
/// (<see cref="SyncEngine.ValuesMatch"/>) and the lookup (<see cref="HeldAttributeValues"/>) are both asked, since export
/// staging asks through either.
/// </summary>
[TestFixture]
public class SyncEngineStagingReferenceRuleTests
{
    private const string MemberDn = "CN=Joe Bloggs,OU=Users,DC=panoply,DC=local";
    private const string MemberDnInAnotherCase = "cn=joe bloggs,ou=users,dc=PANOPLY,dc=LOCAL";

    public enum Asked
    {
        ByScan,
        ByLookup
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_AddNamingAnObjectAgainstARowResolvedToAnotherObjectWithItsDnInAnotherCase_IsNotCurrent(Asked asked)
    {
        var member = Member();
        var held = ResolvedRow(member, referencedCsoId: Guid.NewGuid(), dn: MemberDnInAnotherCase);
        var add = NamingAnObject(member, PendingExportAttributeChangeType.Add, Guid.NewGuid(), MemberDn);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.False,
            "the row names another object, so the member is not held and the Add must be staged");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_RemoveNamingAnObjectAgainstARowResolvedToAnotherObjectWithTheSameDn_IsCurrent(Asked asked)
    {
        var member = Member();
        var held = ResolvedRow(member, referencedCsoId: Guid.NewGuid(), dn: MemberDn);
        var remove = NamingAnObject(member, PendingExportAttributeChangeType.Remove, Guid.NewGuid(), MemberDn);

        Assert.That(IsAlreadyCurrent(asked, remove, held), Is.True,
            "the row names another object, so the object being removed is already absent");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_AddNamingTheObjectARowIsResolvedTo_IsCurrentWhateverTheDn(Asked asked)
    {
        var member = Member();
        var objectId = Guid.NewGuid();
        var held = ResolvedRow(member, referencedCsoId: objectId, dn: "CN=Joe Bloggs (renamed),OU=Users,DC=panoply,DC=local");
        var add = NamingAnObject(member, PendingExportAttributeChangeType.Add, objectId, MemberDn);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.True, "the row names the object the Add names");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_AddNamingAnObjectAgainstAnUnresolvedRowWithItsDnInAnotherCase_IsCurrent(Asked asked)
    {
        var member = Member();
        var held = UnresolvedRow(member, MemberDnInAnotherCase);
        var add = NamingAnObject(member, PendingExportAttributeChangeType.Add, Guid.NewGuid(), MemberDn);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.True,
            "an unresolved row is matched by its distinguished name, which ignores case on this attribute");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_CaseExactAttributeAgainstAnUnresolvedRowWithItsDnInAnotherCase_IsNotCurrent(Asked asked)
    {
        var member = Member(caseExact: true);
        var held = UnresolvedRow(member, MemberDnInAnotherCase);
        var add = NamingNoObject(member, PendingExportAttributeChangeType.Add, MemberDn);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.False, "a case-exact attribute compares distinguished names exactly");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_CaseExactAttributeAgainstAnUnresolvedRowWithTheSameDn_IsCurrent(Asked asked)
    {
        var member = Member(caseExact: true);
        var held = UnresolvedRow(member, MemberDn);
        var add = NamingNoObject(member, PendingExportAttributeChangeType.Add, MemberDn);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.True);
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_AddNamingNoObjectAgainstAResolvedRowWithItsDnInAnotherCase_IsCurrent(Asked asked)
    {
        var member = Member();
        var held = ResolvedRow(member, referencedCsoId: Guid.NewGuid(), dn: MemberDnInAnotherCase);
        var add = NamingNoObject(member, PendingExportAttributeChangeType.Add, MemberDn);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.True,
            "a change naming no object (a reference recall's distinguished name) has only its text, and matches any row by it");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_AddNamingAMetaverseObjectAgainstARowResolvedToItsObject_IsCurrent(Asked asked)
    {
        var member = Member();
        var memberMvoId = Guid.NewGuid();
        var held = ResolvedRow(member, referencedCsoId: Guid.NewGuid(), dn: MemberDn, referencedMvoId: memberMvoId);
        var add = NamingAMetaverseObject(member, PendingExportAttributeChangeType.Add, memberMvoId);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.True, "the row names the Metaverse Object the staged Add names");
    }

    [TestCase(Asked.ByScan)]
    [TestCase(Asked.ByLookup)]
    public void IsCsoAttributeAlreadyCurrent_AddNamingAMetaverseObjectAgainstARowResolvedToAnUnjoinedObject_IsNotCurrent(Asked asked)
    {
        var member = Member();
        var memberMvoId = Guid.NewGuid();
        var held = ResolvedRow(member, referencedCsoId: Guid.NewGuid(), dn: memberMvoId.ToString());
        var add = NamingAMetaverseObject(member, PendingExportAttributeChangeType.Add, memberMvoId);

        Assert.That(IsAlreadyCurrent(asked, add, held), Is.False,
            "a resolved row names its own object, which is joined to no Metaverse Object, so it is never matched by text");
    }

    /// <summary>
    /// The case the issue describes, end to end through export staging: a group holds a row resolved to a stale object
    /// whose distinguished name equals the new member's apart from letter case. Staging names the member by its
    /// Metaverse Object, so the Add is staged.
    /// </summary>
    [Test]
    public void ComputeAttributeValueChanges_GroupHoldsAStaleObjectWithTheNewMembersDnInAnotherCase_StagesTheAdd()
    {
        var source = new MetaverseAttribute { Id = 1, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var target = Member();
        var rule = new SyncRule { Id = 7, Name = "Export", Direction = SyncRuleDirection.Export, Enabled = true };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 70,
            SyncRule = rule,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Id = 700, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });

        var newMemberMvoId = Guid.NewGuid();
        var group = new MetaverseObject { Id = Guid.NewGuid() };
        var member = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = group, Attribute = source, AttributeId = source.Id, ReferenceValueId = newMemberMvoId };
        group.AttributeValues.Add(member);
        var groupCso = new ConnectedSystemObject { Id = Guid.NewGuid() };

        var staleRow = ResolvedRow(target, referencedCsoId: Guid.NewGuid(), dn: MemberDnInAnotherCase, referencedMvoId: Guid.NewGuid());
        staleRow.ConnectedSystemObject = groupCso;
        var held = new[] { staleRow }.ToLookup(value => (groupCso.Id, value.AttributeId));

        var changes = new SyncEngine().ComputeAttributeValueChanges(group, rule, [member], PendingExportChangeType.Update,
            existingCso: groupCso, csoAttributeCache: held, out var alreadyCurrentCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1), "the new member is not held, so its Add is staged");
            Assert.That(changes[0].UnresolvedReferenceValue, Is.EqualTo(newMemberMvoId.ToString()));
            Assert.That(alreadyCurrentCount, Is.Zero);
        }
    }

    private static bool IsAlreadyCurrent(Asked asked, PendingExportAttributeValueChange change, ConnectedSystemObjectAttributeValue held) =>
        asked == Asked.ByScan
            ? SyncEngine.IsCsoAttributeAlreadyCurrent(change, [held])
            : SyncEngine.IsCsoAttributeAlreadyCurrent(change, new HeldAttributeValues([held]));

    private static ConnectedSystemObjectTypeAttribute Member(bool caseExact = false) => new()
    {
        Id = 2,
        Name = "member",
        Type = AttributeDataType.Reference,
        AttributePlurality = AttributePlurality.MultiValued,
        CaseExact = caseExact
    };

    private static ConnectedSystemObjectAttributeValue ResolvedRow(ConnectedSystemObjectTypeAttribute attribute, Guid referencedCsoId, string dn, Guid? referencedMvoId = null) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        ReferenceValue = new ConnectedSystemObject { Id = referencedCsoId, MetaverseObjectId = referencedMvoId },
        ReferenceValueId = referencedCsoId,
        UnresolvedReferenceValue = dn
    };

    private static ConnectedSystemObjectAttributeValue UnresolvedRow(ConnectedSystemObjectTypeAttribute attribute, string dn) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        UnresolvedReferenceValue = dn
    };

    // As export resolution leaves a change: the object resolved to, and its distinguished name as text.
    private static PendingExportAttributeValueChange NamingAnObject(ConnectedSystemObjectTypeAttribute attribute,
        PendingExportAttributeChangeType changeType, Guid objectId, string dn) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        ChangeType = changeType,
        StringValue = dn,
        ResolvedReferenceCsoId = objectId
    };

    // As reference recall stages a change: the distinguished name alone.
    private static PendingExportAttributeValueChange NamingNoObject(ConnectedSystemObjectTypeAttribute attribute,
        PendingExportAttributeChangeType changeType, string dn) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        ChangeType = changeType,
        StringValue = dn
    };

    // As export staging stages a change: the Metaverse Object the reference names, resolved at export.
    private static PendingExportAttributeValueChange NamingAMetaverseObject(ConnectedSystemObjectTypeAttribute attribute,
        PendingExportAttributeChangeType changeType, Guid metaverseObjectId) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        ChangeType = changeType,
        UnresolvedReferenceValue = metaverseObjectId.ToString()
    };
}
