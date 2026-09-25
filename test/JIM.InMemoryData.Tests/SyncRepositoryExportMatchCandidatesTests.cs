// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// <see cref="SyncRepository.GetExportMatchCandidateIdsAsync"/> is the in-memory mirror of the
/// PostgreSQL batch export-matching candidate query: same eligibility rules as
/// <see cref="SyncRepository.FindConnectedSystemObjectUsingMatchingRuleAsync"/>, but resolves the
/// Connected System attribute by name (matching the batch query's name-based join) across a whole set
/// of values in one call.
/// </summary>
[TestFixture]
public class SyncRepositoryExportMatchCandidatesTests
{
    private const int CsId = 1;
    private const int OtherCsId = 2;
    private const int ObjectTypeId = 100;
    private const int OtherObjectTypeId = 101;
    private const string AttrName = "employeeId";

    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static ConnectedSystemObjectTypeAttribute CreateAttribute(string name = AttrName, AttributeDataType type = AttributeDataType.Text)
        => new() { Id = 1, Name = name, Type = type };

    private ConnectedSystemObject SeedCso(
        ConnectedSystemObjectTypeAttribute attribute,
        object? value,
        int connectedSystemId = CsId,
        int typeId = ObjectTypeId,
        Guid? metaverseObjectId = null,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var av = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute };
        switch (value)
        {
            case string s: av.StringValue = s; break;
            case int i: av.IntValue = i; break;
            case long l: av.LongValue = l; break;
            case decimal d: av.DecimalValue = d; break;
            case Guid g: av.GuidValue = g; break;
        }

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = typeId,
            MetaverseObjectId = metaverseObjectId,
            Status = status,
            AttributeValues = [av]
        };

        _repo.SeedConnectedSystemObject(cso);
        return cso;
    }

    #region Empty input / unsupported type

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_EmptyValues_ReturnsEmptyAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "E12345");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: []);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetExportMatchCandidateIdsAsync_UnsupportedDataType_ThrowsArgumentException()
    {
        Assert.That(
            () => _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Boolean, caseSensitive: true, values: ["x"]),
            Throws.ArgumentException);
    }

    #endregion

    #region Per-type matching

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_TextType_ReturnsMatchingCsoAsync()
    {
        var attribute = CreateAttribute();
        var cso = SeedCso(attribute, "E12345");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"E12345", cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_NumberType_ReturnsMatchingCsoAsync()
    {
        var attribute = CreateAttribute(type: AttributeDataType.Number);
        var cso = SeedCso(attribute, 42);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Number, caseSensitive: true, values: [42]);

        Assert.That(result, Is.EqualTo(new[] { ((object)42, cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_LongNumberType_ReturnsMatchingCsoAsync()
    {
        var attribute = CreateAttribute(type: AttributeDataType.LongNumber);
        var cso = SeedCso(attribute, 9_000_000_000L);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.LongNumber, caseSensitive: true, values: [9_000_000_000L]);

        Assert.That(result, Is.EqualTo(new[] { ((object)9_000_000_000L, cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_DecimalType_ReturnsMatchingCsoAsync()
    {
        var attribute = CreateAttribute(type: AttributeDataType.Decimal);
        var cso = SeedCso(attribute, 5.00m);

        // Scale-insensitive equality, matching PostgreSQL numeric comparison (5.0 = 5.00).
        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Decimal, caseSensitive: true, values: [5.0m]);

        Assert.That(result, Is.EqualTo(new[] { ((object)5.0m, cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_GuidType_ReturnsMatchingCsoAsync()
    {
        var guid = Guid.NewGuid();
        var attribute = CreateAttribute(type: AttributeDataType.Guid);
        var cso = SeedCso(attribute, guid);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Guid, caseSensitive: true, values: [guid]);

        Assert.That(result, Is.EqualTo(new[] { ((object)guid, cso.Id) }));
    }

    #endregion

    #region Case sensitivity, exact equality (no wildcards)

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_CaseInsensitive_MatchesDifferentCasingAsync()
    {
        var attribute = CreateAttribute();
        var cso = SeedCso(attribute, "ESMITH");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: false, values: ["esmith"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"esmith", cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_CaseSensitive_DoesNotMatchDifferentCasingAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "ESMITH");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["esmith"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_CaseInsensitive_UnderscoreValueDoesNotWildcardMatchAsync()
    {
        // The wildcard fix: a value like "j_smith" must not match "jxsmith" under a
        // case-insensitive comparison. Only an exact (case-folded) match is a hit.
        var attribute = CreateAttribute();
        SeedCso(attribute, "jxsmith");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: false, values: ["j_smith"]);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Eligibility exclusions

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_JoinedCso_IsExcludedAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "E12345", metaverseObjectId: Guid.NewGuid());

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_ObsoleteCso_IsExcludedAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "E12345", status: ConnectedSystemObjectStatus.Obsolete);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_PendingProvisioningCso_IsExcludedAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "E12345", status: ConnectedSystemObjectStatus.PendingProvisioning);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_OtherConnectedSystem_IsExcludedAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "E12345", connectedSystemId: OtherCsId);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_OtherObjectType_IsExcludedAsync()
    {
        var attribute = CreateAttribute();
        SeedCso(attribute, "E12345", typeId: OtherObjectTypeId);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_OtherAttributeName_IsExcludedAsync()
    {
        var attribute = CreateAttribute(name: "otherAttribute");
        SeedCso(attribute, "E12345");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Ordering and value identity

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_MultipleValues_OrderedByValueThenCsoIdAsync()
    {
        var attribute = CreateAttribute();
        var second = SeedCso(attribute, "B");
        var first = SeedCso(attribute, "A");

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["B", "A"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"A", first.Id), ((object)"B", second.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_ReturnedValue_IsTheExactInputElementAsync()
    {
        // Two distinct boxed instances carrying the same value: the returned Value must be the exact
        // element from the input collection, not a freshly-derived value from the database column.
        var attribute = CreateAttribute();
        var cso = SeedCso(attribute, "E12345");
        var inputValue = new string("E12345".ToCharArray());

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: [inputValue]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(ReferenceEquals(result[0].Value, inputValue), Is.True);
            Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(cso.Id));
        }
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_MultipleCsosMatchSameValue_ReturnsBothOrderedByIdAsync()
    {
        var attribute = CreateAttribute();

        // Seed twice with the same value; keep whichever comes back first/second by id to assert ordering.
        var csoA = SeedCso(attribute, "E12345");
        var csoB = SeedCso(attribute, "E12345");
        var (firstId, secondId) = csoA.Id.CompareTo(csoB.Id) <= 0 ? (csoA.Id, csoB.Id) : (csoB.Id, csoA.Id);

        var result = await _repo.GetExportMatchCandidateIdsAsync(CsId, ObjectTypeId, AttrName, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"E12345", firstId), ((object)"E12345", secondId) }));
    }

    #endregion
}
