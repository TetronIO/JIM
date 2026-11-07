// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Utilities;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// #2705: a Delta Import looks up only the External IDs a page returns. For those keys the answer must be exactly what
/// the whole-system lookup (<see cref="SyncRepository.GetAllCsoImportStateLookupAsync"/>) holds, or an object JIM holds
/// would read as new and be created a second time.
/// </summary>
[TestFixture]
public class SyncRepositoryCsoImportStateLookupByExternalIdsTests
{
    private const int CsId = 1;
    private const int OtherCsId = 2;
    private const int ObjectTypeId = 100;

    private SyncRepository _repo = null!;
    private ConnectedSystemObjectTypeAttribute _anchorIgnoringCase = null!;
    private ConnectedSystemObjectTypeAttribute _caseExactAnchor = null!;
    private ConnectedSystemObjectTypeAttribute _secondary = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
        _anchorIgnoringCase = new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "accountId", Type = AttributeDataType.Text, IsExternalId = true };
        _caseExactAnchor = new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "code", Type = AttributeDataType.Text, IsExternalId = true, CaseExact = true };
        _secondary = new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true };
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_KnownAndUnknownKeys_ReturnsOnlyTheHeldObjectAsync()
    {
        var held = Seed(CsId, _anchorIgnoringCase, "JBloggs", status: ConnectedSystemObjectStatus.Normal, partitionId: 7);
        Seed(CsId, _anchorIgnoringCase, "Someone Else");

        var lookup = await _repo.GetCsoImportStateLookupByExternalIdsAsync(CsId,
            [new CsoExternalIdProbe(_anchorIgnoringCase.Id, false, "jbloggs"), new CsoExternalIdProbe(_anchorIgnoringCase.Id, false, "nobody")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup.Keys, Is.EquivalentTo(new[] { CsoLookupKey.For(CsId, _anchorIgnoringCase.Id, "jbloggs") }));
            Assert.That(lookup.Values.Single(), Is.EqualTo(new CsoImportStateLookupEntry(held.Id, null, null, ConnectedSystemObjectStatus.Normal, 7)));
        }
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_CaseExactAnchor_FindsOnlyTheValueInItsOwnCaseAsync()
    {
        var upper = Seed(CsId, _caseExactAnchor, "JBloggs");
        Seed(CsId, _caseExactAnchor, "jbloggs");

        var lookup = await _repo.GetCsoImportStateLookupByExternalIdsAsync(CsId,
            [new CsoExternalIdProbe(_caseExactAnchor.Id, true, "JBloggs"), new CsoExternalIdProbe(_caseExactAnchor.Id, true, "JBLOGGS")]);

        Assert.That(lookup.ToDictionary(e => e.Key, e => e.Value.CsoId),
            Is.EquivalentTo(new Dictionary<string, Guid> { [CsoLookupKey.For(CsId, _caseExactAnchor.Id, "JBloggs")] = upper.Id }));
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_ObjectWithNoExternalIdYet_IsFoundByItsSecondaryExternalIdAsync()
    {
        var pending = Seed(CsId, _anchorIgnoringCase, value: null, secondaryValue: "CN=Joe", status: ConnectedSystemObjectStatus.PendingProvisioning);
        Seed(CsId, _anchorIgnoringCase, "joe-id", secondaryValue: "CN=Jane");

        var lookup = await _repo.GetCsoImportStateLookupByExternalIdsAsync(CsId,
            [new CsoExternalIdProbe(_secondary.Id, false, "cn=joe"), new CsoExternalIdProbe(_secondary.Id, false, "cn=jane")]);

        Assert.That(lookup.ToDictionary(e => e.Key, e => e.Value.CsoId),
            Is.EquivalentTo(new Dictionary<string, Guid> { [CsoLookupKey.For(CsId, _secondary.Id, "cn=joe")] = pending.Id }),
            "An object with an External ID is keyed by it alone, as the whole lookup keys it");
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_ObjectInAnotherConnectedSystem_IsNotFoundAsync()
    {
        Seed(OtherCsId, _anchorIgnoringCase, "JBloggs");

        var lookup = await _repo.GetCsoImportStateLookupByExternalIdsAsync(CsId, [new CsoExternalIdProbe(_anchorIgnoringCase.Id, false, "jbloggs")]);

        Assert.That(lookup, Is.Empty);
    }

    [Test]
    public async Task GetCsoImportStateLookupByExternalIdsAsync_EveryKeyOfTheWholeLookup_AnswersAsTheWholeLookupDoesAsync()
    {
        Seed(CsId, _anchorIgnoringCase, "JBloggs");
        Seed(CsId, _caseExactAnchor, "Abc");
        Seed(CsId, _anchorIgnoringCase, value: null, secondaryValue: "CN=Pending", status: ConnectedSystemObjectStatus.PendingProvisioning);
        var whole = await _repo.GetAllCsoImportStateLookupAsync(CsId);

        var probes = whole.Keys.Select(ProbeFor).ToList();
        var lookup = await _repo.GetCsoImportStateLookupByExternalIdsAsync(CsId, probes);

        Assert.That(lookup, Is.EquivalentTo(whole));
    }

    [Test]
    public void GetCsoImportStateLookupByExternalIdsAsync_KeyIgnoringCaseOutsideAscii_Throws()
    {
        Assert.That(async () => await _repo.GetCsoImportStateLookupByExternalIdsAsync(CsId, [new CsoExternalIdProbe(_anchorIgnoringCase.Id, false, "émile")]),
            Throws.ArgumentException);
    }

    private CsoExternalIdProbe ProbeFor(string key)
    {
        // cso:{cs}:{attribute}:{value}
        var parts = key.Split(':', 4);
        var attributeId = int.Parse(parts[2]);
        return new CsoExternalIdProbe(attributeId, attributeId == _caseExactAnchor.Id, parts[3]);
    }

    private ConnectedSystemObject Seed(int connectedSystemId, ConnectedSystemObjectTypeAttribute anchor, string? value, string? secondaryValue = null,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal, int? partitionId = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = anchor.Id,
            SecondaryExternalIdAttributeId = secondaryValue != null ? _secondary.Id : null,
            Status = status,
            PartitionId = partitionId,
            Created = DateTime.UtcNow
        };
        if (value != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = anchor.Id, Attribute = anchor, StringValue = value });
        if (secondaryValue != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _secondary.Id, Attribute = _secondary, StringValue = secondaryValue });

        _repo.SeedConnectedSystemObject(cso);
        return cso;
    }
}
