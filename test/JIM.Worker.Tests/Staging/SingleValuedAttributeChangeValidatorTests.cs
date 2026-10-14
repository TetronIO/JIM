// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Staging;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Connectors;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Staging;

/// <summary>
/// The last check before an export is handed to a Connector (#2182): an export carrying more than one change for one
/// single-valued attribute contradicts itself, because export staging gives a single-valued attribute exactly one
/// change carrying the value it should hold. Such an export is refused against its own object rather than sent, so a
/// regression in staging or merging can never reach a Connected System, where it either fails in the database's terms
/// or, worse, applies in an order that clears the value.
/// </summary>
[TestFixture]
public class SingleValuedAttributeChangeValidatorTests
{
    #region Check

    [Test]
    public void Check_TwoChangesForOneSingleValuedAttribute_RefusesNamingIt()
    {
        var mail = Attribute(40, "mail", AttributePlurality.SingleValued);

        var refusal = SingleValuedAttributeChangeValidator.Check(PendingExportWith(
            Change(mail, PendingExportAttributeChangeType.Add, "b@example.com"),
            Change(mail, PendingExportAttributeChangeType.Remove, "a@example.com")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.Not.Null);
            Assert.That(refusal!.Success, Is.False);
            Assert.That(refusal!.ErrorMessage, Does.Contain("'mail'"));
        }
    }

    [Test]
    public void Check_SeveralContradictedAttributes_NamesEachOnceInNameOrder()
    {
        var title = Attribute(42, "title", AttributePlurality.SingleValued);
        var mail = Attribute(40, "mail", AttributePlurality.SingleValued);

        var refusal = SingleValuedAttributeChangeValidator.Check(PendingExportWith(
            Change(title, PendingExportAttributeChangeType.Update, "Engineer"),
            Change(mail, PendingExportAttributeChangeType.Update, "b@example.com"),
            Change(title, PendingExportAttributeChangeType.Update, "Architect"),
            Change(mail, PendingExportAttributeChangeType.Remove, "a@example.com"),
            Change(mail, PendingExportAttributeChangeType.Add, "c@example.com")));

        Assert.That(refusal?.ErrorMessage, Does.Contain("'mail', 'title'"));
    }

    [Test]
    public void Check_OneChangeForEachSingleValuedAttribute_Permits()
    {
        var mail = Attribute(40, "mail", AttributePlurality.SingleValued);
        var title = Attribute(42, "title", AttributePlurality.SingleValued);

        var refusal = SingleValuedAttributeChangeValidator.Check(PendingExportWith(
            Change(mail, PendingExportAttributeChangeType.Update, "b@example.com"),
            Change(title, PendingExportAttributeChangeType.Update, null)));

        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Check_SeveralChangesForOneMultiValuedAttribute_Permits()
    {
        var member = Attribute(41, "member", AttributePlurality.MultiValued);

        var refusal = SingleValuedAttributeChangeValidator.Check(PendingExportWith(
            Change(member, PendingExportAttributeChangeType.Add, "CN=Ada,OU=Staff,DC=example,DC=com"),
            Change(member, PendingExportAttributeChangeType.Remove, "CN=Grace,OU=Staff,DC=example,DC=com")));

        Assert.That(refusal, Is.Null);
    }

    #endregion

    #region Before the batch is sent

    [Test]
    public async Task ExportBatchAsync_AnExportCarryingTwoChangesForOneSingleValuedAttribute_IsRefusedNamingItAndNeverSentAsync()
    {
        var mail = Attribute(40, "mail", AttributePlurality.SingleValued);
        var contradictory = PendingExportWith(
            Change(mail, PendingExportAttributeChangeType.Add, "b@example.com"),
            Change(mail, PendingExportAttributeChangeType.Remove, "a@example.com"));
        var sound = PendingExportWith(Change(mail, PendingExportAttributeChangeType.Update, "c@example.com"));
        var (connector, sent) = RecordingConnector();

        var (results, classMembershipRefused, contradictionsRefused) = await ExportExecutionServer.ExportBatchAsync(connector,
            new ConnectedSystem { Id = 1, Name = "HR Directory" }, [contradictory, sound], CancellationToken.None, new RecordingConnectorProgress());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(contradictionsRefused, Is.EqualTo(1));
            Assert.That(classMembershipRefused, Is.Zero,
                "a contradiction is a fault in JIM, not the configuration gap a class membership refusal points an administrator at");
            Assert.That(results, Has.Count.EqualTo(2), "results stay positional, one per export in the batch");
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("'mail'"));
            Assert.That(results[1].Success, Is.True);
            Assert.That(sent, Is.EqualTo(new[] { sound }), "only the export that does not contradict itself reaches the Connector");
        }
    }

    [Test]
    public async Task ExportBatchAsync_SeveralChangesForOneMultiValuedAttribute_AreSentAsTheyAreAsync()
    {
        var member = Attribute(41, "member", AttributePlurality.MultiValued);
        var membership = PendingExportWith(
            Change(member, PendingExportAttributeChangeType.Add, "CN=Ada,OU=Staff,DC=example,DC=com"),
            Change(member, PendingExportAttributeChangeType.Remove, "CN=Grace,OU=Staff,DC=example,DC=com"));
        var (connector, sent) = RecordingConnector();

        var (results, classMembershipRefused, contradictionsRefused) = await ExportExecutionServer.ExportBatchAsync(connector,
            new ConnectedSystem { Id = 1, Name = "HR Directory" }, [membership], CancellationToken.None, new RecordingConnectorProgress());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classMembershipRefused, Is.Zero);
            Assert.That(contradictionsRefused, Is.Zero);
            Assert.That(results.Single().Success, Is.True);
            Assert.That(sent, Is.EqualTo(new[] { membership }));
        }
    }

    #endregion

    #region Helpers

    private static (IConnectorExportUsingCalls Connector, List<PendingExport> Sent) RecordingConnector()
    {
        var sent = new List<PendingExport>();
        var connector = new Mock<IConnectorExportUsingCalls>();
        connector
            .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                sent.AddRange(exports);
                return [.. exports.Select(_ => ConnectedSystemExportResult.Succeeded())];
            });

        return (connector.Object, sent);
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(int id, string name, AttributePlurality plurality) => new()
    {
        Id = id,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = plurality
    };

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectTypeAttribute attribute, PendingExportAttributeChangeType changeType, string? value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        ChangeType = changeType,
        StringValue = value
    };

    private static PendingExport PendingExportWith(params PendingExportAttributeValueChange[] changes)
    {
        var connectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid() };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = connectedSystemObject,
            ConnectedSystemObjectId = connectedSystemObject.Id,
            AttributeValueChanges = [.. changes]
        };
    }

    #endregion
}
