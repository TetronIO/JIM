// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// Error mapping on export against a real domain controller (#1853). The connector treats result 20
/// (attributeOrValueExists) as "the member is already present, so the desired state holds"; Active Directory is
/// believed to answer a duplicate member add with result 68 (entryAlreadyExists) instead, which Samba AD does not.
/// </summary>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectoryExportProbeTests
{
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public async Task Export_AddingAMemberThatIsAlreadyPresent_IsTreatedAsSuccessAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        string groupDn;
        string contactDn;
        using (var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger))
        {
            contactDn = ActiveDirectoryLab.EnsureContacts(admin, lab, 1)[0];
            groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Membership Group", "jim-probe-membership", [contactDn]);
        }

        var objectType = ActiveDirectoryLab.GroupObjectType();
        var dnAttribute = objectType.Attributes.Single(attribute => attribute.Name == "distinguishedName");
        var memberAttribute = objectType.Attributes.Single(attribute => attribute.Name == "member");
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = objectType,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { AttributeId = dnAttribute.Id, StringValue = groupDn }]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = memberAttribute,
                    AttributeId = memberAttribute.Id,
                    ChangeType = PendingExportAttributeChangeType.Add,
                    UnresolvedReferenceValue = contactDn
                }
            ]
        };

        using var connector = ActiveDirectoryLab.NewConnector(lab);
        connector.OpenExportConnection(ActiveDirectoryLab.ConnectorSettings(lab), null);
        List<ConnectedSystemExportResult> results;
        try
        {
            results = await connector.ExportAsync([pendingExport], CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True,
            $"Adding a member that is already present must be reported as success, since the desired state holds. The directory said: {results[0].ErrorMessage}");
    }
}
