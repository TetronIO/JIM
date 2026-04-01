using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.EvaluatePendingExportConfirmation — no mocking, no database.
/// </summary>
public class SyncEnginePendingExportConfirmationTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void EvaluatePendingExportConfirmation_NullDictionary_ReturnsNone()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };

        var result = _engine.EvaluatePendingExportConfirmation(cso, null);

        Assert.That(result.HasResults, Is.False);
    }

    [Test]
    public void EvaluatePendingExportConfirmation_NoPendingExportsForCso_ReturnsNone()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var dict = new Dictionary<Guid, List<PendingExport>>();

        var result = _engine.EvaluatePendingExportConfirmation(cso, dict);

        Assert.That(result.HasResults, Is.False);
    }

    [Test]
    public void EvaluatePendingExportConfirmation_SkipsPendingStatus()
    {
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Pending,
            AttributeValueChanges = [new PendingExportAttributeValueChange
            {
                AttributeId = 1,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Type = AttributeDataType.Text },
                StringValue = "test"
            }]
        };
        var dict = new Dictionary<Guid, List<PendingExport>> { [csoId] = [pe] };

        var result = _engine.EvaluatePendingExportConfirmation(cso, dict);

        Assert.That(result.HasResults, Is.False);
    }

    [Test]
    public void EvaluatePendingExportConfirmation_AllChangesConfirmed_MarksForDeletion()
    {
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, StringValue = "expectedValue" });

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.ExportNotConfirmed,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    AttributeId = 1,
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Type = AttributeDataType.Text },
                    StringValue = "expectedValue",
                    ChangeType = PendingExportAttributeChangeType.Add
                }
            ]
        };
        var dict = new Dictionary<Guid, List<PendingExport>> { [csoId] = [pe] };

        var result = _engine.EvaluatePendingExportConfirmation(cso, dict);

        Assert.That(result.HasResults, Is.True);
        Assert.That(result.ToDelete.Count, Is.EqualTo(1));
        Assert.That(result.ToUpdate.Count, Is.EqualTo(0));
    }

    [Test]
    public void EvaluatePendingExportConfirmation_NoChangesConfirmed_MarksForUpdate()
    {
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, StringValue = "wrongValue" });

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.ExportNotConfirmed,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    AttributeId = 1,
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Type = AttributeDataType.Text },
                    StringValue = "expectedValue",
                    ChangeType = PendingExportAttributeChangeType.Add
                }
            ]
        };
        var dict = new Dictionary<Guid, List<PendingExport>> { [csoId] = [pe] };

        var result = _engine.EvaluatePendingExportConfirmation(cso, dict);

        Assert.That(result.HasResults, Is.True);
        Assert.That(result.ToDelete.Count, Is.EqualTo(0));
        Assert.That(result.ToUpdate.Count, Is.EqualTo(1));
        Assert.That(pe.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
        Assert.That(pe.ErrorCount, Is.EqualTo(1));
    }
}
