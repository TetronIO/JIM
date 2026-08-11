// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// A Decimal anchor must be indexed by the Connected System Object lookup, and keyed canonically (#1283).
///
/// Oracle's <c>NUMBER</c> is discovered as <see cref="AttributeDataType.Decimal"/>, so a sequence-backed
/// primary key (the ordinary case) arrives as a decimal. The lookup that maps an external ID value to
/// the Connected System Object JIM already holds for that row did not consider decimals at all: the key
/// came out null, the object was never indexed, and the following import matched nothing and created a
/// duplicate. Nothing was reported, because a missing key is indistinguishable from a new object.
/// </summary>
[TestFixture]
public class SyncRepositoryDecimalExternalIdTests
{
    private SyncRepository _repo = null!;

    private const int CsId = 1;
    private const int AttrId = 10;
    private const int ObjectTypeId = 100;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static ConnectedSystemObject CreateCsoWithDecimalAnchor(Guid id, decimal anchor)
    {
        return new ConnectedSystemObject
        {
            Id = id,
            ConnectedSystemId = CsId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = AttrId,
            Created = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    AttributeId = AttrId,
                    DecimalValue = anchor
                }
            }
        };
    }

    [Test]
    public async Task GetAllCsoExternalIdMappingsAsync_DecimalAnchor_IndexesTheObjectAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCsoWithDecimalAnchor(csoId, decimal.Parse("4200", CultureInfo.InvariantCulture)));

        var mappings = await _repo.GetAllCsoExternalIdMappingsAsync(CsId);

        Assert.That(mappings, Is.Not.Empty, "a Decimal-anchored object must be indexed, or every import duplicates it");
        Assert.That(mappings.ContainsKey($"cso:{CsId}:{AttrId}:4200"), Is.True);
        Assert.That(mappings[$"cso:{CsId}:{AttrId}:4200"], Is.EqualTo(csoId));
    }

    [Test]
    public async Task GetAllCsoExternalIdMappingsAsync_DecimalAnchorWithTrailingZeros_KeysCanonicallyAsync()
    {
        // The database can hand back the same numeric anchor at a different scale to the one the
        // Connector read on import. The key must not depend on that, or the object silently
        // fails to match itself.
        var csoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCsoWithDecimalAnchor(csoId, decimal.Parse("4200.00", CultureInfo.InvariantCulture)));

        var mappings = await _repo.GetAllCsoExternalIdMappingsAsync(CsId);

        Assert.That(mappings.ContainsKey($"cso:{CsId}:{AttrId}:4200"), Is.True);
    }

    [Test]
    public async Task GetAllCsoImportStateLookupAsync_DecimalAnchor_IndexesTheObjectAsync()
    {
        // The import-state lookup carries the skip predicate's inputs and is keyed identically.
        var csoId = Guid.NewGuid();
        _repo.SeedConnectedSystemObject(CreateCsoWithDecimalAnchor(csoId, decimal.Parse("17.5", CultureInfo.InvariantCulture)));

        var lookup = await _repo.GetAllCsoImportStateLookupAsync(CsId);

        Assert.That(lookup.ContainsKey($"cso:{CsId}:{AttrId}:17.5"), Is.True);
        Assert.That(lookup[$"cso:{CsId}:{AttrId}:17.5"].CsoId, Is.EqualTo(csoId));
    }
}
