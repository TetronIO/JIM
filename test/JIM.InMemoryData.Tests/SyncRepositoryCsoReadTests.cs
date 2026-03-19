using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryCsoReadTests
{
    private SyncRepository _repo = null!;

    private const int CsId = 1;
    private const int AttrId = 10;
    private const int SecondaryAttrId = 11;
    private const int ObjectTypeId = 100;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private ConnectedSystemObject CreateCso(
        Guid? id = null,
        int connectedSystemId = CsId,
        int typeId = ObjectTypeId,
        int externalIdAttributeId = AttrId,
        DateTime? created = null,
        DateTime? lastUpdated = null,
        Guid? metaverseObjectId = null,
        int? secondaryExternalIdAttributeId = null)
    {
        return new ConnectedSystemObject
        {
            Id = id ?? Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = typeId,
            ExternalIdAttributeId = externalIdAttributeId,
            SecondaryExternalIdAttributeId = secondaryExternalIdAttributeId,
            Created = created ?? DateTime.UtcNow,
            LastUpdated = lastUpdated,
            MetaverseObjectId = metaverseObjectId,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
    }

    private ConnectedSystemObjectAttributeValue CreateAv(
        int attributeId,
        string? stringValue = null,
        int? intValue = null,
        Guid? guidValue = null,
        long? longValue = null,
        string? unresolvedRef = null)
    {
        return new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attributeId,
            StringValue = stringValue,
            IntValue = intValue,
            GuidValue = guidValue,
            LongValue = longValue,
            UnresolvedReferenceValue = unresolvedRef
        };
    }

    #region Count

    [Test]
    public async Task GetConnectedSystemObjectCountAsync_EmptyRepo_ReturnsZeroAsync()
    {
        var count = await _repo.GetConnectedSystemObjectCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetConnectedSystemObjectCountAsync_WithCsos_ReturnsCountAsync()
    {
        _repo.SeedConnectedSystemObject(CreateCso());
        _repo.SeedConnectedSystemObject(CreateCso());
        _repo.SeedConnectedSystemObject(CreateCso(connectedSystemId: 2));

        var count = await _repo.GetConnectedSystemObjectCountAsync(CsId);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_FiltersCorrectlyAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        _repo.SeedConnectedSystemObject(CreateCso(lastUpdated: DateTime.UtcNow));
        _repo.SeedConnectedSystemObject(CreateCso(lastUpdated: DateTime.UtcNow.AddHours(-2)));
        _repo.SeedConnectedSystemObject(CreateCso()); // No LastUpdated

        var count = await _repo.GetConnectedSystemObjectModifiedSinceCountAsync(CsId, cutoff);
        Assert.That(count, Is.EqualTo(1));
    }

    #endregion

    #region Paging

    [Test]
    public async Task GetConnectedSystemObjectsAsync_ReturnsPagedResultAsync()
    {
        var created = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            _repo.SeedConnectedSystemObject(CreateCso(created: created.AddMinutes(i)));

        var page1 = await _repo.GetConnectedSystemObjectsAsync(CsId, 1, 2);
        Assert.That(page1.Results, Has.Count.EqualTo(2));
        Assert.That(page1.TotalResults, Is.EqualTo(5));
        Assert.That(page1.CurrentPage, Is.EqualTo(1));
        Assert.That(page1.PageSize, Is.EqualTo(2));

        var page3 = await _repo.GetConnectedSystemObjectsAsync(CsId, 3, 2);
        Assert.That(page3.Results, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_FiltersAndPagesAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 3; i++)
            _repo.SeedConnectedSystemObject(CreateCso(lastUpdated: DateTime.UtcNow, created: DateTime.UtcNow.AddMinutes(i)));
        _repo.SeedConnectedSystemObject(CreateCso(lastUpdated: DateTime.UtcNow.AddHours(-2)));

        var result = await _repo.GetConnectedSystemObjectsModifiedSinceAsync(CsId, cutoff, 1, 10);
        Assert.That(result.TotalResults, Is.EqualTo(3));
        Assert.That(result.Results, Has.Count.EqualTo(3));
    }

    #endregion

    #region Single CSO by ID

    [Test]
    public async Task GetConnectedSystemObjectAsync_Found_ReturnsCsoAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCso(id: csoId));

        var result = await _repo.GetConnectedSystemObjectAsync(CsId, csoId);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(csoId));
    }

    [Test]
    public async Task GetConnectedSystemObjectAsync_WrongSystem_ReturnsNullAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCso(id: csoId));

        var result = await _repo.GetConnectedSystemObjectAsync(999, csoId);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectAsync_NotFound_ReturnsNullAsync()
    {
        var result = await _repo.GetConnectedSystemObjectAsync(CsId, Guid.NewGuid());
        Assert.That(result, Is.Null);
    }

    #endregion

    #region Attribute Lookups

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_Int_FindsCsoAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, intValue: 42));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectByAttributeAsync(CsId, AttrId, 42);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(cso.Id));
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_String_FindsCsoAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, stringValue: "john.doe"));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectByAttributeAsync(CsId, AttrId, "john.doe");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_Guid_FindsCsoAsync()
    {
        var guidVal = Guid.NewGuid();
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, guidValue: guidVal));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectByAttributeAsync(CsId, AttrId, guidVal);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_Long_FindsCsoAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, longValue: 123456789L));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectByAttributeAsync(CsId, AttrId, 123456789L);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_NotFound_ReturnsNullAsync()
    {
        var result = await _repo.GetConnectedSystemObjectByAttributeAsync(CsId, AttrId, "nonexistent");
        Assert.That(result, Is.Null);
    }

    #endregion

    #region Secondary External ID

    [Test]
    public async Task GetConnectedSystemObjectBySecondaryExternalIdAsync_FindsCsoAsync()
    {
        var cso = CreateCso(secondaryExternalIdAttributeId: SecondaryAttrId);
        cso.AttributeValues.Add(CreateAv(SecondaryAttrId, stringValue: "CN=John,DC=test"));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectBySecondaryExternalIdAsync(CsId, ObjectTypeId, "CN=John,DC=test");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync_FindsCsoAsync()
    {
        var cso = CreateCso(secondaryExternalIdAttributeId: SecondaryAttrId);
        cso.AttributeValues.Add(CreateAv(SecondaryAttrId, stringValue: "DN123"));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(CsId, "DN123");
        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region Batch Lookups

    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_ReturnsDictionaryAsync()
    {
        var cso1 = CreateCso();
        cso1.AttributeValues.Add(CreateAv(AttrId, stringValue: "user1"));
        var cso2 = CreateCso();
        cso2.AttributeValues.Add(CreateAv(AttrId, stringValue: "user2"));
        _repo.SeedConnectedSystemObject(cso1);
        _repo.SeedConnectedSystemObject(cso2);

        var result = await _repo.GetConnectedSystemObjectsByAttributeValuesAsync(
            CsId, AttrId, new[] { "user1", "user2", "nonexistent" });
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.ContainsKey("user1"), Is.True);
        Assert.That(result.ContainsKey("user2"), Is.True);
    }

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync_ReturnsDictionaryAsync()
    {
        var cso = CreateCso(secondaryExternalIdAttributeId: SecondaryAttrId);
        cso.AttributeValues.Add(CreateAv(SecondaryAttrId, stringValue: "DN1"));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(
            CsId, new[] { "DN1", "DN2" });
        Assert.That(result, Has.Count.EqualTo(1));
    }

    #endregion

    #region External ID Value Lists

    [Test]
    public async Task GetAllExternalIdAttributeValuesOfTypeIntAsync_ReturnsValuesAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, intValue: 42));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetAllExternalIdAttributeValuesOfTypeIntAsync(CsId, ObjectTypeId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(42));
    }

    [Test]
    public async Task GetAllExternalIdAttributeValuesOfTypeStringAsync_ReturnsValuesAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, stringValue: "ext1"));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetAllExternalIdAttributeValuesOfTypeStringAsync(CsId, ObjectTypeId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("ext1"));
    }

    [Test]
    public async Task GetAllExternalIdAttributeValuesOfTypeGuidAsync_ReturnsValuesAsync()
    {
        var guidVal = Guid.NewGuid();
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, guidValue: guidVal));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetAllExternalIdAttributeValuesOfTypeGuidAsync(CsId, ObjectTypeId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(guidVal));
    }

    [Test]
    public async Task GetAllExternalIdAttributeValuesOfTypeLongAsync_ReturnsValuesAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(CreateAv(AttrId, longValue: 99L));
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetAllExternalIdAttributeValuesOfTypeLongAsync(CsId, ObjectTypeId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(99L));
    }

    #endregion

    #region Reference Resolution

    [Test]
    public async Task GetConnectedSystemObjectsForReferenceResolutionAsync_ReturnsMatchingCsosAsync()
    {
        var cso1 = CreateCso();
        var cso2 = CreateCso();
        _repo.SeedConnectedSystemObject(cso1);
        _repo.SeedConnectedSystemObject(cso2);

        var result = await _repo.GetConnectedSystemObjectsForReferenceResolutionAsync(
            new List<Guid> { cso1.Id, Guid.NewGuid() });
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(cso1.Id));
    }

    [Test]
    public async Task GetReferenceExternalIdsAsync_ReturnsUnresolvedReferencesAsync()
    {
        var cso = CreateCso();
        var av = CreateAv(AttrId, unresolvedRef: "target-ext-id");
        cso.AttributeValues.Add(av);
        _repo.SeedConnectedSystemObject(cso);

        var result = await _repo.GetReferenceExternalIdsAsync(cso.Id);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[av.Id], Is.EqualTo("target-ext-id"));
    }

    [Test]
    public async Task GetReferenceExternalIdsAsync_NotFound_ReturnsEmptyAsync()
    {
        var result = await _repo.GetReferenceExternalIdsAsync(Guid.NewGuid());
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region CSO Count by MVO

    [Test]
    public async Task GetConnectedSystemObjectCountByMetaverseObjectIdAsync_ReturnCorrectCountAsync()
    {
        var mvoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCso(metaverseObjectId: mvoId));
        _repo.SeedConnectedSystemObject(CreateCso(metaverseObjectId: mvoId));
        _repo.SeedConnectedSystemObject(CreateCso());

        var count = await _repo.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetConnectedSystemObjectCountByMvoAsync_FiltersbySystemAsync()
    {
        var mvoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCso(metaverseObjectId: mvoId, connectedSystemId: 1));
        _repo.SeedConnectedSystemObject(CreateCso(metaverseObjectId: mvoId, connectedSystemId: 2));

        var count = await _repo.GetConnectedSystemObjectCountByMvoAsync(1, mvoId);
        Assert.That(count, Is.EqualTo(1));
    }

    #endregion
}
