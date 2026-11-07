// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory store's export batch load loads a large group's member attribute in part, as the database's does
/// (#2816), so unit and workflow tests run export execution against the same shape of object production does. What it
/// hands over is a copy of the stored object holding the loaded values, so the stored object keeps every value; writes
/// made through the copy reach the stored object, as writes made through a loaded object reach the database.
/// </summary>
[TestFixture]
public class SyncRepositoryExportBatchLeanLoadTests
{
    private const int ConnectedSystemId = 1;
    private SyncRepository _repo = null!;
    private ConnectedSystemObjectType _groupType = null!;
    private ConnectedSystemObjectTypeAttribute _dn = null!;
    private ConnectedSystemObjectTypeAttribute _member = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
        _groupType = new ConnectedSystemObjectType { Id = 5, Name = "groupOfNames" };
        _dn = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, ConnectedSystemObjectType = _groupType
        };
        _member = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = _groupType
        };
        _groupType.Attributes = [_dn, _member];
        _repo.SeedObjectType(_groupType);
    }

    private (ConnectedSystemObject Group, List<ConnectedSystemObjectAttributeValue> Members) SeedGroup(int memberCount)
    {
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId, Type = _groupType, TypeId = _groupType.Id,
            SecondaryExternalIdAttributeId = _dn.Id
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = _dn, AttributeId = _dn.Id, StringValue = "cn=Staff,ou=groups", ConnectedSystemObject = group
        });
        var members = Enumerable.Range(0, memberCount).Select(i => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = _member, AttributeId = _member.Id, ReferenceValueId = Guid.NewGuid(),
            UnresolvedReferenceValue = $"cn=user{i},ou=people", ConnectedSystemObject = group
        }).ToList();
        group.AttributeValues.AddRange(members);
        _repo.SeedConnectedSystemObject(group);
        return (group, members);
    }

    private PendingExport SeedRemoval(ConnectedSystemObject group, params ConnectedSystemObjectAttributeValue[] removed)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId, ConnectedSystemObject = group, ConnectedSystemObjectId = group.Id,
            ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Pending
        };
        export.AttributeValueChanges.AddRange(removed.Select(member => new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = _member, AttributeId = _member.Id, ChangeType = PendingExportAttributeChangeType.Remove,
            Status = PendingExportAttributeChangeStatus.Pending, StringValue = member.UnresolvedReferenceValue,
            ResolvedReferenceCsoId = member.ReferenceValueId
        }));
        _repo.SeedPendingExport(export);
        return export;
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_LargeGroup_LoadsOnlyTheNamedMembersAndKeepsTheStoredObjectWholeAsync()
    {
        var (group, members) = SeedGroup(10);
        var stored = SeedRemoval(group, members[0], members[1]);

        var loaded = (await _repo.GetPendingExportsByIdsAsync([stored.Id])).Single();

        var cso = loaded.ConnectedSystemObject!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == _member.Id), Is.EquivalentTo(new[] { members[0], members[1] }),
                "only the members the changes name");
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == _dn.Id).Count(), Is.EqualTo(1), "every other value");
            Assert.That(cso.PartiallyLoadedAttributes![_member.Id].AtLeastValueCount, Is.EqualTo(4), "counted up to two changes plus two");
            Assert.That(cso.HeldValueCount("member"), Is.EqualTo(4));
            Assert.That(cso, Is.Not.SameAs(group));
            Assert.That(group.AttributeValues, Has.Count.EqualTo(11), "the stored object keeps every value");
            Assert.That(group.PartiallyLoadedAttributes, Is.Null);
            Assert.That(stored.ConnectedSystemObject, Is.SameAs(group), "the stored Pending Export still names the stored object");
        }
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_GroupTheChangesCouldEmpty_IsLoadedWholeAsIsAsync()
    {
        var (group, members) = SeedGroup(3);
        var stored = SeedRemoval(group, members[0], members[1]);

        var loaded = (await _repo.GetPendingExportsByIdsAsync([stored.Id])).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.SameAs(stored), "three held, below the cap of four");
            Assert.That(loaded.ConnectedSystemObject, Is.SameAs(group));
            Assert.That(group.PartiallyLoadedAttributes, Is.Null);
        }
    }

    [Test]
    public async Task ApplyExportedAttributeValuesAsync_ThroughALeanCopy_ReachesTheStoredObjectAsync()
    {
        var (group, members) = SeedGroup(10);
        var stored = SeedRemoval(group, members[0]);
        var copy = (await _repo.GetPendingExportsByIdsAsync([stored.Id])).Single().ConnectedSystemObject!;
        var addition = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = copy, Attribute = _member, AttributeId = _member.Id, UnresolvedReferenceValue = "cn=new,ou=people"
        };

        await _repo.ApplyExportedAttributeValuesAsync([addition], [members[0].Id], [group.Id]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.AttributeValues, Does.Contain(addition));
            Assert.That(group.AttributeValues, Does.Not.Contain(members[0]));
            Assert.That(group.AttributeValues.Count(v => v.AttributeId == _member.Id), Is.EqualTo(10));
        }
    }

    [Test]
    public async Task UpdatePendingExportsAsync_ALeanCopy_WritesItsStateIntoTheStoredExportAsync()
    {
        var (group, members) = SeedGroup(10);
        var stored = SeedRemoval(group, members[0]);
        var copy = (await _repo.GetPendingExportsByIdsAsync([stored.Id])).Single();
        Assert.That(copy, Is.Not.SameAs(stored));

        copy.Status = PendingExportStatus.Exported;
        copy.ErrorCount = 3;
        await _repo.UpdatePendingExportsAsync([copy]);

        var reloaded = (await _repo.GetPendingExportsAsync(ConnectedSystemId)).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(stored.ErrorCount, Is.EqualTo(3));
            Assert.That(stored.ConnectedSystemObject, Is.SameAs(group), "never the lean copy");
            Assert.That(reloaded, Is.SameAs(stored));
        }
    }

    [Test]
    public void SaveMethodsWritingTheValueSet_PartiallyLoadedObject_Throw()
    {
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId,
            PartiallyLoadedAttributes = new Dictionary<int, PartiallyLoadedAttribute>
            {
                [_member.Id] = new ExportPartiallyLoadedAttribute(_member.Id, "member", 4)
            }
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => _repo.CreateConnectedSystemObjectAsync(group), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => _repo.CreateConnectedSystemObjectsAsync([group]), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => _repo.UpdateConnectedSystemObjectAsync(group), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => _repo.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(group, []), Throws.TypeOf<PartiallyLoadedObjectException>());
        }
    }

    /// <summary>
    /// The update both paths save an object loaded in part through writes its row and the values and removals it is handed
    /// (by id, in PostgreSQL), so it accepts one, and every value the object did not load stays held.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_ObjectLoadedInPart_KeepsTheValuesItDidNotLoadAsync()
    {
        var (group, members) = SeedGroup(10);
        var stored = SeedRemoval(group, members[0]);
        var copy = (await _repo.GetPendingExportsByIdsAsync([stored.Id])).Single().ConnectedSystemObject!;
        Assert.That(copy.IsPartiallyLoaded, Is.True);
        copy.AttributeValues.Remove(members[0]);

        await _repo.UpdateConnectedSystemObjectsAsync([copy], pendingRemovalIds: [members[0].Id]);

        Assert.That(group.AttributeValues.Where(v => v.AttributeId == _member.Id), Is.EquivalentTo(members.Skip(1)));
    }
}
