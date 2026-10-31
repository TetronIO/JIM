// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// Group membership exports whose desired state partly holds already, against a real domain controller (#1853).
/// </summary>
/// <remarks>
/// An LDAP modify is atomic, so a domain controller refuses a whole membership change for one member already present
/// (entryAlreadyExists, 68; "The object exists. Attribute member already exists for target GUID ..." on Samba AD) or
/// one already absent, and applies none of the others it carried. The connector attaches the Permissive Modify
/// control where the directory advertises it, which both Active Directory and Samba AD do; the probes marked
/// "without Permissive Modify" drive the export without it, so the directory's own refusals reach the connector's
/// fallback, which applies the refused modify in parts, exactly as a directory that does not offer the control would send them.
/// </remarks>
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

        var results = await ExportThroughTheConnectorAsync(lab, MembershipChange(groupDn, (PendingExportAttributeChangeType.Add, contactDn)));

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True,
            $"Adding a member that is already present must be reported as success, since the desired state holds. The directory said: {results[0].ErrorMessage}");
    }

    [Test]
    public async Task Export_AddingMembersOfWhichOneIsAlreadyPresent_AddsTheOthersAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 3);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Partial Membership Group", "jim-probe-partial-members", [contacts[0]]);
        EnsureNotMembers(admin, groupDn, contacts[1], contacts[2]);

        var results = await ExportThroughTheConnectorAsync(lab, MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Add, contacts[0]), (PendingExportAttributeChangeType.Add, contacts[1]),
            (PendingExportAttributeChangeType.Add, contacts[2])));

        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        Assert.That(ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member"), Is.SupersetOf(contacts).IgnoreCase);
    }

    [Test]
    public async Task Export_RemovingMembersOfWhichOneIsAlreadyAbsent_RemovesTheOthersAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 3);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Departing Members Group", "jim-probe-departing-members", [contacts[0], contacts[1]]);
        EnsureNotMembers(admin, groupDn, contacts[2]);

        var results = await ExportThroughTheConnectorAsync(lab, MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Remove, contacts[0]), (PendingExportAttributeChangeType.Remove, contacts[2])));

        var members = ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member");
        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members, Does.Not.Contain(contacts[0]).IgnoreCase);
            Assert.That(members, Does.Contain(contacts[1]).IgnoreCase);
        }
    }

    [Test]
    public async Task Export_WithoutPermissiveModify_AddingMembersOfWhichOneIsAlreadyPresent_AddsTheOthersAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 3);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Fallback Membership Group", "jim-probe-fallback-members", [contacts[0]]);
        EnsureNotMembers(admin, groupDn, contacts[1], contacts[2]);
        var export = ExportWithoutPermissiveModify(admin);

        var results = await export.ExecuteAsync([MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Add, contacts[0]), (PendingExportAttributeChangeType.Add, contacts[1]),
            (PendingExportAttributeChangeType.Add, contacts[2]))], CancellationToken.None);

        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member"), Is.SupersetOf(contacts).IgnoreCase);
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Export_WithoutPermissiveModify_RemovingMembersOfWhichOneIsAlreadyAbsent_RemovesTheOthersAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 3);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Fallback Departing Group", "jim-probe-fallback-departing", [contacts[0], contacts[1]]);
        EnsureNotMembers(admin, groupDn, contacts[2]);
        var export = ExportWithoutPermissiveModify(admin);

        var results = await export.ExecuteAsync([MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Remove, contacts[0]), (PendingExportAttributeChangeType.Remove, contacts[2]))], CancellationToken.None);

        var members = ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member");
        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members, Does.Not.Contain(contacts[0]).IgnoreCase);
            Assert.That(members, Does.Contain(contacts[1]).IgnoreCase);
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Export_RemovingAMemberTheGroupHeldOnceAndHasLost_RemovesTheOthersAsync()
    {
        // Samba AD keeps the link of a member taken out of a group, and answers the removal of that member again with
        // "Attribute member already deleted for target GUID" rather than with the "doesn't exist" it gives for a member
        // the group never held. Both mean the member is already gone.
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 3);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Lost Member Group", "jim-probe-lost-member", [contacts[0], contacts[1], contacts[2]]);
        EnsureNotMembers(admin, groupDn, contacts[0]);

        var results = await ExportThroughTheConnectorAsync(lab, MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Remove, contacts[0]), (PendingExportAttributeChangeType.Remove, contacts[1])));

        var members = ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member");
        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members, Does.Not.Contain(contacts[1]).IgnoreCase);
            Assert.That(members, Does.Contain(contacts[2]).IgnoreCase);
        }
    }

    [Test]
    public async Task Export_AddingAMemberUnderANewCaseWhoseOldSpellingIsAlreadyGone_AddsTheMemberAsync()
    {
        // The export removes a member's old spelling and adds the new one, but the group no longer holds the member at
        // all. Samba AD refuses the removal of an absent member even under Permissive Modify, so the connector applies the
        // change in parts. The directory resolves a member name without regard to case, so an add of the new spelling sent
        // on its own first is accepted, and the removal of the old spelling sent after it takes the member away again.
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 2);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Member Respelt Group", "jim-probe-member-respelt", [contacts[1]]);
        EnsureNotMembers(admin, groupDn, contacts[0]);

        var results = await ExportThroughTheConnectorAsync(lab, MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Remove, contacts[0]), (PendingExportAttributeChangeType.Add, contacts[0].ToLowerInvariant())));

        var members = ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member");
        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members, Does.Contain(contacts[0]).IgnoreCase, "The member the export adds under a new case is in the group");
            Assert.That(members, Does.Contain(contacts[1]).IgnoreCase);
        }
    }

    [Test]
    public async Task Export_KeepingAMemberUnderANewCaseWhileRemovingOneAlreadyAbsent_KeepsTheMemberAsync()
    {
        // Samba AD refuses the removal of a member the group does not hold even under Permissive Modify, so the connector
        // applies the change in parts, and the member kept under a new case must survive the removal of its old spelling.
        // Samba refuses the add of the new spelling outright (entryAlreadyExists) rather than skipping it, so against Samba
        // this passes whichever is sent first; a directory that skips it is covered in
        // LdapConnectorExportDesiredStateTests. This guards that the pair, sent together, is accepted.
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, 3);
        var groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Member Case Group", "jim-probe-member-case", [contacts[0], contacts[1]]);
        EnsureNotMembers(admin, groupDn, contacts[2]);

        var results = await ExportThroughTheConnectorAsync(lab, MembershipChange(groupDn,
            (PendingExportAttributeChangeType.Remove, contacts[2]), (PendingExportAttributeChangeType.Remove, contacts[0]),
            (PendingExportAttributeChangeType.Add, contacts[0].ToLowerInvariant())));

        var members = ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member");
        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members, Does.Contain(contacts[0]).IgnoreCase, "The member kept under a new case is still in the group");
            Assert.That(members, Does.Contain(contacts[1]).IgnoreCase);
            Assert.That(members, Does.Not.Contain(contacts[2]).IgnoreCase);
        }
    }

    [Test]
    public async Task Export_ChangingTheCaseOfAProxyAddress_KeepsTheAddressAsync()
    {
        // Active Directory compares proxyAddresses without regard to case, so under Permissive Modify an add of the new
        // spelling sent before the delete of the old one was skipped as already present, and the delete then took the
        // address away while the export reported success. Samba AD compares them exactly and never skips the add, so
        // against Samba this passes with or without the fix; it guards the case against a Windows domain controller,
        // and LdapConnectorExportDesiredStateTests covers it in every build.
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var userDn = SeedProxyAddresses(admin, lab, OldPrimaryAddress);

        var results = await ExportThroughTheConnectorAsync(lab, RenameChange(userDn));

        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        Assert.That(ActiveDirectoryLab.ReadAllValues(admin, userDn, ProxyAddresses), Is.EquivalentTo(ExpectedAfterRename).IgnoreCase);
    }

    [Test]
    public async Task Export_WithoutPermissiveModify_ChangingTheCaseOfAProxyAddress_KeepsTheAddressAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var userDn = SeedProxyAddresses(admin, lab, OldPrimaryAddress);
        var export = ExportWithoutPermissiveModify(admin);

        var results = await export.ExecuteAsync([RenameChange(userDn)], CancellationToken.None);

        Assert.That(results.Single().Success, Is.True, $"The directory said: {results.Single().ErrorMessage}");
        Assert.That(ActiveDirectoryLab.ReadAllValues(admin, userDn, ProxyAddresses), Is.EquivalentTo(ExpectedAfterRename).IgnoreCase);
    }

    private async Task<List<ConnectedSystemExportResult>> ExportThroughTheConnectorAsync(ActiveDirectoryLab.Coordinates lab, PendingExport pendingExport)
    {
        using var connector = ActiveDirectoryLab.NewConnector(lab);
        connector.OpenExportConnection(ActiveDirectoryLab.ConnectorSettings(lab), null);
        try
        {
            return await connector.ExportAsync([pendingExport], CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    /// <summary>
    /// The connector's export logic over a raw connection, told the directory does not offer Permissive Modify.
    /// </summary>
    private LdapConnectorExport ExportWithoutPermissiveModify(LdapConnection connection)
    {
        var directoryType = LdapConnectorUtilities.GetBasicRootDseInformation(connection, _logger).DirectoryType;
        List<ConnectedSystemSettingValue> settings =
        [
            new() { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }
        ];

        return new LdapConnectorExport(new LdapOperationExecutor(connection), settings, _logger,
            directoryType: directoryType, directoryAdvertisesPermissiveModify: false);
    }

    /// <summary>
    /// Takes members out of a fixture group if a previous run left them there. Reads the group first and removes only
    /// those it holds: Samba AD refuses the removal of an absent member even under Permissive Modify.
    /// </summary>
    private static void EnsureNotMembers(LdapConnection connection, string groupDn, params string[] memberDns)
    {
        var held = new HashSet<string>(ActiveDirectoryLab.ReadAllValues(connection, groupDn, "member"), StringComparer.OrdinalIgnoreCase);
        var present = memberDns.Where(held.Contains).Cast<object>().ToArray();
        if (present.Length > 0)
            connection.SendRequest(new ModifyRequest(groupDn, DirectoryAttributeOperation.Delete, "member", present));
    }

    private const string ProxyAddresses = "proxyAddresses";
    private const string OldPrimaryAddress = "SMTP:jim.probe.old@probe.example";
    private const string NewPrimaryAddress = "SMTP:jim.probe.new@probe.example";
    private const string OldAddressAsAlias = "smtp:jim.probe.old@probe.example";
    private static readonly string[] ExpectedAfterRename = [NewPrimaryAddress, OldAddressAsAlias];

    /// <summary>
    /// A user of its own, holding exactly the given addresses. A user rather than a contact, because the delegation
    /// JIM's service account works under covers users, groups and organisational units.
    /// </summary>
    private static string SeedProxyAddresses(LdapConnection connection, ActiveDirectoryLab.Coordinates lab, params string[] addresses)
    {
        var userDn = ActiveDirectoryLab.EnsureUser(connection, lab, "JIM Probe Address Rename User", "jim-probe-rename");
        connection.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, ProxyAddresses, addresses.Cast<object>().ToArray()));
        return userDn;
    }

    /// <summary>
    /// What a rename stages for the addresses: the new primary added, the old one kept as an alias (a spelling that
    /// differs only in case), and the old primary removed, in the order JIM stages them, additions first.
    /// </summary>
    private static PendingExport RenameChange(string userDn)
    {
        var objectType = new ConnectedSystemObjectType { Id = 2, Name = "user", Selected = true };
        objectType.Attributes.AddRange(
        [
            ActiveDirectoryLab.Attribute(objectType, 11, "objectGUID", AttributeDataType.Guid, isExternalId: true),
            ActiveDirectoryLab.Attribute(objectType, 12, "distinguishedName", AttributeDataType.Text, isSecondaryExternalId: true),
            ActiveDirectoryLab.Attribute(objectType, 13, ProxyAddresses, AttributeDataType.Text, AttributePlurality.MultiValued)
        ]);
        var dnAttribute = objectType.Attributes.Single(attribute => attribute.Name == "distinguishedName");
        var proxyAttribute = objectType.Attributes.Single(attribute => attribute.Name == ProxyAddresses);

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = objectType,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { AttributeId = dnAttribute.Id, StringValue = userDn }]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange { Attribute = proxyAttribute, AttributeId = proxyAttribute.Id, ChangeType = PendingExportAttributeChangeType.Add, StringValue = NewPrimaryAddress },
                new PendingExportAttributeValueChange { Attribute = proxyAttribute, AttributeId = proxyAttribute.Id, ChangeType = PendingExportAttributeChangeType.Add, StringValue = OldAddressAsAlias },
                new PendingExportAttributeValueChange { Attribute = proxyAttribute, AttributeId = proxyAttribute.Id, ChangeType = PendingExportAttributeChangeType.Remove, StringValue = OldPrimaryAddress }
            ]
        };
    }

    private static PendingExport MembershipChange(string groupDn, params (PendingExportAttributeChangeType ChangeType, string MemberDn)[] changes)
    {
        var objectType = ActiveDirectoryLab.GroupObjectType();
        var dnAttribute = objectType.Attributes.Single(attribute => attribute.Name == "distinguishedName");
        var memberAttribute = objectType.Attributes.Single(attribute => attribute.Name == "member");

        return new PendingExport
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
            AttributeValueChanges = new(changes.Select(change => new PendingExportAttributeValueChange
            {
                Attribute = memberAttribute,
                AttributeId = memberAttribute.Id,
                ChangeType = change.ChangeType,
                UnresolvedReferenceValue = change.MemberDn
            }))
        };
    }
}
