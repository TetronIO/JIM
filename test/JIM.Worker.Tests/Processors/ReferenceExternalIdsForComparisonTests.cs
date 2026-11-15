// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Moq;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// #2521: an import asks the database, once per page, for the External IDs of the objects each matched object references,
/// to compare its references with what the Connector returned. The question reads only references the objects hold, and
/// the objects were loaded with every value they hold, so a page of objects that hold none (a page of users in a directory
/// whose users carry no manager) has an answer without asking: at 500,000 users in pages of 100, 5,000 round trips.
/// </summary>
[TestFixture]
public class ReferenceExternalIdsForComparisonTests
{
    private const int MemberAttributeId = 7;
    private const int NameAttributeId = 3;

    [Test]
    public async Task GetReferenceExternalIdsForComparisonAsync_NoObjectHoldsAReference_AsksTheDatabaseNothingAsync()
    {
        // Arrange
        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        var csos = new[] { ObjectHolding(reference: null), ObjectHolding(reference: null) };

        // Act
        var result = await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(syncRepo.Object, csos);

        // Assert
        Assert.That(result.Keys, Is.EquivalentTo(csos.Select(c => c.Id)), "Every object still has an entry, as callers rely on");
        Assert.That(result.Values.All(externalIds => externalIds.Count == 0), Is.True);
    }

    [Test]
    public async Task GetReferenceExternalIdsForComparisonAsync_SomeObjectsHoldReferences_AsksOnlyAboutThoseAsync()
    {
        // Arrange
        var referenced = Guid.NewGuid();
        var withReference = ObjectHolding(reference: referenced);
        var withoutReference = ObjectHolding(reference: null);
        IReadOnlyCollection<Guid>? askedAbout = null;

        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        syncRepo.Setup(r => r.GetReferenceExternalIdsForCsosAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<int>?>()))
            .Callback<IReadOnlyCollection<Guid>, IReadOnlyCollection<int>?>((ids, _) => askedAbout = ids.ToList())
            .ReturnsAsync(new Dictionary<Guid, Dictionary<Guid, string>>
            {
                [withReference.Id] = new() { [referenced] = "CN=Manager" }
            });

        // Act
        var result = await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(syncRepo.Object, [withReference, withoutReference]);

        // Assert
        Assert.That(askedAbout, Is.EquivalentTo(new[] { withReference.Id }));
        Assert.That(result[withReference.Id], Is.EquivalentTo(new Dictionary<Guid, string> { [referenced] = "CN=Manager" }));
        Assert.That(result[withoutReference.Id], Is.Empty);
    }

    private static ConnectedSystemObject ObjectHolding(Guid? reference)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = NameAttributeId, StringValue = "Someone", ConnectedSystemObject = cso });
        if (reference != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = MemberAttributeId, ReferenceValueId = reference, ConnectedSystemObject = cso });
        return cso;
    }
}
