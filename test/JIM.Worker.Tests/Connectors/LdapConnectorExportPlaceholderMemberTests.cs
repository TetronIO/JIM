using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Reflection;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for groupOfNames/groupOfUniqueNames placeholder member handling in the LDAP connector.
/// The groupOfNames object class (RFC 4519) requires at least one member value (MUST constraint).
/// When a group has no real members, the connector injects a placeholder DN to satisfy this constraint.
/// </summary>
[TestFixture]
public class LdapConnectorExportPlaceholderMemberTests
{
    private Mock<ILdapOperationExecutor> _mockExecutor = null!;
    private IList<ConnectedSystemSettingValue> _defaultSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _mockExecutor = new Mock<ILdapOperationExecutor>();
        _defaultSettings = new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" },
                StringValue = "Delete"
            }
        };
    }

    #region RequiresPlaceholderMember tests

    [Test]
    public void RequiresPlaceholderMember_OpenLDAP_GroupOfNames_ReturnsTrueAsync()
    {
        var export = CreateOpenLdapExport();
        var pendingExport = CreateGroupOfNamesCreatePendingExport("cn=testgroup,ou=groups,dc=test,dc=local", 0);

        var result = export.RequiresPlaceholderMember(pendingExport);

        Assert.That(result, Is.True);
    }

    [Test]
    public void RequiresPlaceholderMember_OpenLDAP_GroupOfUniqueNames_ReturnsTrueAsync()
    {
        var export = CreateOpenLdapExport();
        var pendingExport = CreateGroupCreatePendingExportWithObjectClass(
            "cn=testgroup,ou=groups,dc=test,dc=local", "groupOfUniqueNames", 0);

        var result = export.RequiresPlaceholderMember(pendingExport);

        Assert.That(result, Is.True);
    }

    [Test]
    public void RequiresPlaceholderMember_OpenLDAP_InetOrgPerson_ReturnsFalseAsync()
    {
        var export = CreateOpenLdapExport();
        var pendingExport = CreatePendingExportWithObjectClass("inetOrgPerson");

        var result = export.RequiresPlaceholderMember(pendingExport);

        Assert.That(result, Is.False);
    }

    [Test]
    public void RequiresPlaceholderMember_ActiveDirectory_Group_ReturnsFalseAsync()
    {
        var export = CreateExport(directoryType: LdapDirectoryType.ActiveDirectory);
        var pendingExport = CreateGroupOfNamesCreatePendingExport("cn=testgroup,ou=groups,dc=test,dc=local", 0);

        var result = export.RequiresPlaceholderMember(pendingExport);

        Assert.That(result, Is.False);
    }

    [Test]
    public void RequiresPlaceholderMember_SambaAD_Group_ReturnsFalseAsync()
    {
        var export = CreateExport(directoryType: LdapDirectoryType.SambaAD);
        var pendingExport = CreateGroupOfNamesCreatePendingExport("cn=testgroup,ou=groups,dc=test,dc=local", 0);

        var result = export.RequiresPlaceholderMember(pendingExport);

        Assert.That(result, Is.False);
    }

    [Test]
    public void RequiresPlaceholderMember_Generic_GroupOfNames_ReturnsTrueAsync()
    {
        var export = CreateExport(directoryType: LdapDirectoryType.Generic);
        var pendingExport = CreateGroupOfNamesCreatePendingExport("cn=testgroup,ou=groups,dc=test,dc=local", 0);

        var result = export.RequiresPlaceholderMember(pendingExport);

        Assert.That(result, Is.True);
    }

    #endregion

    #region GetMemberAttributeName tests

    [Test]
    public void GetMemberAttributeName_GroupOfNames_ReturnsMemberAsync()
    {
        var result = LdapConnectorExport.GetMemberAttributeName("groupOfNames");

        Assert.That(result, Is.EqualTo("member"));
    }

    [Test]
    public void GetMemberAttributeName_GroupOfUniqueNames_ReturnsUniqueMemberAsync()
    {
        var result = LdapConnectorExport.GetMemberAttributeName("groupOfUniqueNames");

        Assert.That(result, Is.EqualTo("uniqueMember"));
    }

    #endregion

    #region BuildAddRequestWithOverflow — placeholder injection on create

    [Test]
    public void BuildAddRequestWithOverflow_GroupOfNames_NoMembers_InjectsPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var dn = "cn=emptygroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesCreatePendingExport(dn, memberCount: 0);

        var (addRequest, _) = export.BuildAddRequestWithOverflow(pendingExport, dn);

        var memberAttr = GetDirectoryAttribute(addRequest, "member");
        Assert.That(memberAttr, Is.Not.Null, "member attribute should be present on the AddRequest");
        Assert.That(memberAttr!.Count, Is.EqualTo(1));
        Assert.That(memberAttr[0]!.ToString(), Is.EqualTo("cn=placeholder"));
    }

    [Test]
    public void BuildAddRequestWithOverflow_GroupOfNames_WithMembers_DoesNotInjectPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var dn = "cn=fullgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesCreatePendingExport(dn, memberCount: 3);

        var (addRequest, _) = export.BuildAddRequestWithOverflow(pendingExport, dn);

        var memberAttr = GetDirectoryAttribute(addRequest, "member");
        Assert.That(memberAttr, Is.Not.Null);
        Assert.That(memberAttr!.Count, Is.EqualTo(3));

        // Verify none of the values is the placeholder
        for (var i = 0; i < memberAttr.Count; i++)
            Assert.That(memberAttr[i]!.ToString(), Does.Not.Contain("placeholder"));
    }

    [Test]
    public void BuildAddRequestWithOverflow_GroupOfNames_NoMembers_CustomPlaceholder_UsesCustomValueAsync()
    {
        var customPlaceholder = "cn=dummy,dc=test,dc=local";
        var export = CreateOpenLdapExport(placeholderDn: customPlaceholder);
        var dn = "cn=emptygroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesCreatePendingExport(dn, memberCount: 0);

        var (addRequest, _) = export.BuildAddRequestWithOverflow(pendingExport, dn);

        var memberAttr = GetDirectoryAttribute(addRequest, "member");
        Assert.That(memberAttr, Is.Not.Null);
        Assert.That(memberAttr!.Count, Is.EqualTo(1));
        Assert.That(memberAttr[0]!.ToString(), Is.EqualTo(customPlaceholder));
    }

    [Test]
    public void BuildAddRequestWithOverflow_ActiveDirectory_NoMembers_DoesNotInjectPlaceholderAsync()
    {
        var export = CreateExport(directoryType: LdapDirectoryType.ActiveDirectory);
        var dn = "CN=EmptyGroup,OU=Groups,DC=test,DC=local";
        var pendingExport = CreateAdGroupCreatePendingExport(dn, memberCount: 0);

        var (addRequest, _) = export.BuildAddRequestWithOverflow(pendingExport, dn);

        var memberAttr = GetDirectoryAttribute(addRequest, "member");
        Assert.That(memberAttr, Is.Null, "AD groups should not get a placeholder member");
    }

    [Test]
    public void BuildAddRequestWithOverflow_GroupOfUniqueNames_NoMembers_InjectsPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var dn = "cn=emptygroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupCreatePendingExportWithObjectClass(dn, "groupOfUniqueNames", memberCount: 0);

        var (addRequest, _) = export.BuildAddRequestWithOverflow(pendingExport, dn);

        var memberAttr = GetDirectoryAttribute(addRequest, "uniqueMember");
        Assert.That(memberAttr, Is.Not.Null, "uniqueMember attribute should be present on the AddRequest");
        Assert.That(memberAttr!.Count, Is.EqualTo(1));
        Assert.That(memberAttr[0]!.ToString(), Is.EqualTo("cn=placeholder"));
    }

    #endregion

    #region BuildModifyRequests — placeholder on last member removal

    [Test]
    public void BuildModifyRequests_RemoveLastMember_GroupOfNames_InjectsPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var groupDn = "cn=testgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesMemberRemovePendingExport(
            groupDn,
            membersToRemove: ["cn=user1,ou=people,dc=test,dc=local"],
            currentMemberCount: 1);

        var requests = export.BuildModifyRequests(pendingExport, groupDn);

        Assert.That(requests, Has.Count.GreaterThanOrEqualTo(1));

        // Should contain both an Add of the placeholder and a Delete of the real member
        var allMods = requests.SelectMany(r =>
            Enumerable.Range(0, r.Modifications.Count).Select(i => r.Modifications[i])).ToList();

        var addPlaceholder = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Add);
        Assert.That(addPlaceholder, Is.Not.Null, "Should add placeholder member");
        Assert.That(addPlaceholder![0]!.ToString(), Is.EqualTo("cn=placeholder"));

        var removeMember = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Delete);
        Assert.That(removeMember, Is.Not.Null, "Should still remove the real member");
    }

    [Test]
    public void BuildModifyRequests_RemoveNonLastMember_GroupOfNames_DoesNotInjectPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var groupDn = "cn=testgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesMemberRemovePendingExport(
            groupDn,
            membersToRemove: ["cn=user1,ou=people,dc=test,dc=local"],
            currentMemberCount: 3);

        var requests = export.BuildModifyRequests(pendingExport, groupDn);

        var allMods = requests.SelectMany(r =>
            Enumerable.Range(0, r.Modifications.Count).Select(i => r.Modifications[i])).ToList();

        var addPlaceholder = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Add);
        Assert.That(addPlaceholder, Is.Null, "Should NOT add placeholder — group still has other members");
    }

    [Test]
    public void BuildModifyRequests_RemoveAllMembers_GroupOfNames_InjectsPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var groupDn = "cn=testgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesMemberRemovePendingExport(
            groupDn,
            membersToRemove:
            [
                "cn=user1,ou=people,dc=test,dc=local",
                "cn=user2,ou=people,dc=test,dc=local",
                "cn=user3,ou=people,dc=test,dc=local"
            ],
            currentMemberCount: 3);

        var requests = export.BuildModifyRequests(pendingExport, groupDn);

        var allMods = requests.SelectMany(r =>
            Enumerable.Range(0, r.Modifications.Count).Select(i => r.Modifications[i])).ToList();

        var addPlaceholder = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Add);
        Assert.That(addPlaceholder, Is.Not.Null, "Should add placeholder when all members removed");
    }

    [Test]
    public void BuildModifyRequests_RemoveLastMember_ActiveDirectory_DoesNotInjectPlaceholderAsync()
    {
        var export = CreateExport(directoryType: LdapDirectoryType.ActiveDirectory);
        var groupDn = "CN=TestGroup,OU=Groups,DC=test,DC=local";
        var pendingExport = CreateAdGroupMemberRemovePendingExport(
            groupDn,
            membersToRemove: ["CN=User1,OU=Users,DC=test,DC=local"],
            currentMemberCount: 1);

        var requests = export.BuildModifyRequests(pendingExport, groupDn);

        var allMods = requests.SelectMany(r =>
            Enumerable.Range(0, r.Modifications.Count).Select(i => r.Modifications[i])).ToList();

        var addPlaceholder = allMods.FirstOrDefault(m =>
            m.Operation == DirectoryAttributeOperation.Add);
        Assert.That(addPlaceholder, Is.Null, "AD groups should not get placeholder handling");
    }

    #endregion

    #region BuildModifyRequests — placeholder removal when adding first real member

    [Test]
    public void BuildModifyRequests_AddMemberToPlaceholderOnlyGroup_RemovesPlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var groupDn = "cn=testgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesMemberAddToPlaceholderGroupPendingExport(
            groupDn,
            membersToAdd: ["cn=user1,ou=people,dc=test,dc=local"]);

        var requests = export.BuildModifyRequests(pendingExport, groupDn);

        var allMods = requests.SelectMany(r =>
            Enumerable.Range(0, r.Modifications.Count).Select(i => r.Modifications[i])).ToList();

        var addMember = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Add);
        Assert.That(addMember, Is.Not.Null, "Should add the real member");

        var removePlaceholder = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Delete);
        Assert.That(removePlaceholder, Is.Not.Null, "Should remove the placeholder member");
        Assert.That(removePlaceholder![0]!.ToString(), Is.EqualTo("cn=placeholder"));
    }

    [Test]
    public void BuildModifyRequests_AddMemberToGroupWithRealMembers_DoesNotRemovePlaceholderAsync()
    {
        var export = CreateOpenLdapExport();
        var groupDn = "cn=testgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesMemberAddPendingExport(
            groupDn,
            membersToAdd: ["cn=user3,ou=people,dc=test,dc=local"],
            currentMemberCount: 2);

        var requests = export.BuildModifyRequests(pendingExport, groupDn);

        var allMods = requests.SelectMany(r =>
            Enumerable.Range(0, r.Modifications.Count).Select(i => r.Modifications[i])).ToList();

        var removePlaceholder = allMods.FirstOrDefault(m =>
            m.Name.Equals("member", StringComparison.OrdinalIgnoreCase) &&
            m.Operation == DirectoryAttributeOperation.Delete);
        Assert.That(removePlaceholder, Is.Null, "Should NOT remove placeholder — group already has real members");
    }

    #endregion

    #region Refint error handling tests

    [Test]
    public void ProcessCreate_PlaceholderRejected_ReturnsConstraintViolationErrorAsync()
    {
        // Simulate OpenLDAP with refint overlay rejecting the placeholder DN
        _mockExecutor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(
                CreateDirectoryResponse<AddResponse>(ResultCode.ConstraintViolation)));

        var export = CreateOpenLdapExport();
        var dn = "cn=emptygroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesCreatePendingExport(dn, memberCount: 0);

        var results = export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None).Result;

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.False);
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PlaceholderMemberConstraintViolation));
        Assert.That(results[0].ErrorMessage, Does.Contain("placeholder"));
        Assert.That(results[0].ErrorMessage, Does.Contain("referential integrity"));
    }

    [Test]
    public void ProcessUpdate_PlaceholderAddRejected_OnLastMemberRemoval_ReturnsConstraintViolationErrorAsync()
    {
        // The modify request that adds the placeholder + removes last member fails
        _mockExecutor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Throws(new DirectoryOperationException(
                CreateDirectoryResponse<ModifyResponse>(ResultCode.ConstraintViolation)));

        var export = CreateOpenLdapExport();
        var groupDn = "cn=testgroup,ou=groups,dc=test,dc=local";
        var pendingExport = CreateGroupOfNamesMemberRemovePendingExport(
            groupDn,
            membersToRemove: ["cn=user1,ou=people,dc=test,dc=local"],
            currentMemberCount: 1);

        var results = export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None).Result;

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.False);
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PlaceholderMemberConstraintViolation));
        Assert.That(results[0].ErrorMessage, Does.Contain("placeholder"));
    }

    #endregion

    #region Helper methods

    private LdapConnectorExport CreateExport(
        int batchSize = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
        int concurrency = 1,
        LdapDirectoryType directoryType = LdapDirectoryType.ActiveDirectory,
        string? placeholderDn = null)
    {
        return new LdapConnectorExport(
            _mockExecutor.Object,
            _defaultSettings,
            Log.Logger,
            concurrency,
            batchSize,
            directoryType,
            placeholderDn);
    }

    private LdapConnectorExport CreateOpenLdapExport(
        int batchSize = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
        string? placeholderDn = null)
    {
        return CreateExport(
            batchSize: batchSize,
            directoryType: LdapDirectoryType.OpenLDAP,
            placeholderDn: placeholderDn);
    }

    /// <summary>
    /// Creates a PendingExport for creating a groupOfNames with the specified number of members.
    /// </summary>
    private static PendingExport CreateGroupOfNamesCreatePendingExport(string groupDn, int memberCount)
    {
        return CreateGroupCreatePendingExportWithObjectClass(groupDn, "groupOfNames", memberCount);
    }

    /// <summary>
    /// Creates a PendingExport for creating an AD 'group' with the specified number of members.
    /// </summary>
    private static PendingExport CreateAdGroupCreatePendingExport(string groupDn, int memberCount)
    {
        return CreateGroupCreatePendingExportWithObjectClass(groupDn, "group", memberCount);
    }

    /// <summary>
    /// Creates a PendingExport for creating a group with a specific objectClass and member count.
    /// </summary>
    private static PendingExport CreateGroupCreatePendingExportWithObjectClass(string groupDn, string objectClass, int memberCount)
    {
        var csoType = new ConnectedSystemObjectType { Name = objectClass };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
            { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType };

        var memberAttrName = objectClass.Equals("groupOfUniqueNames", StringComparison.OrdinalIgnoreCase) ? "uniqueMember" : "member";
        var memberAttr = new ConnectedSystemObjectTypeAttribute
            { Id = 10, Name = memberAttrName, ConnectedSystemObjectType = csoType, AttributePlurality = AttributePlurality.MultiValued };

        var changes = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = dnAttr, ChangeType = PendingExportAttributeChangeType.Add, StringValue = groupDn }
        };

        for (var i = 0; i < memberCount; i++)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = $"cn=user{i:D4},ou=people,dc=test,dc=local"
            });
        }

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = csoType },
            AttributeValueChanges = changes
        };
    }

    /// <summary>
    /// Creates a PendingExport for a non-group object class (e.g. inetOrgPerson).
    /// </summary>
    private static PendingExport CreatePendingExportWithObjectClass(string objectClass)
    {
        var csoType = new ConnectedSystemObjectType { Name = objectClass };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = csoType },
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
    }

    /// <summary>
    /// Creates a PendingExport for removing members from a groupOfNames.
    /// Uses CSO attribute values to represent the current member state, so the connector
    /// can determine whether removal would leave the group empty.
    /// </summary>
    private static PendingExport CreateGroupOfNamesMemberRemovePendingExport(
        string groupDn,
        string[] membersToRemove,
        int currentMemberCount)
    {
        var csoType = new ConnectedSystemObjectType { Name = "groupOfNames" };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10, Name = "member", ConnectedSystemObjectType = csoType,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var changes = new List<PendingExportAttributeValueChange>();
        foreach (var member in membersToRemove)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Remove,
                StringValue = member
            });
        }

        // Build the CSO with current member values so the connector knows the current state
        var dnAttr = new ConnectedSystemObjectTypeAttribute
            { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType };

        var csoAttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                AttributeId = dnAttr.Id,
                StringValue = groupDn
            }
        };

        // Add member attribute values representing the current state
        // Each member is a separate ConnectedSystemObjectAttributeValue with UnresolvedReferenceValue
        for (var i = 0; i < currentMemberCount; i++)
        {
            var memberDn = i < membersToRemove.Length
                ? membersToRemove[i]
                : $"cn=otheruser{i:D4},ou=people,dc=test,dc=local";

            csoAttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = memberAttr.Id,
                Attribute = memberAttr,
                UnresolvedReferenceValue = memberDn
            });
        }

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType,
                SecondaryExternalIdAttributeId = dnAttr.Id,
                AttributeValues = csoAttributeValues
            },
            AttributeValueChanges = changes
        };
    }

    /// <summary>
    /// Creates a PendingExport for removing members from an AD group.
    /// </summary>
    private static PendingExport CreateAdGroupMemberRemovePendingExport(
        string groupDn,
        string[] membersToRemove,
        int currentMemberCount)
    {
        var csoType = new ConnectedSystemObjectType { Name = "group" };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10, Name = "member", ConnectedSystemObjectType = csoType,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var changes = new List<PendingExportAttributeValueChange>();
        foreach (var member in membersToRemove)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Remove,
                StringValue = member
            });
        }

        var dnAttr = new ConnectedSystemObjectTypeAttribute
            { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType,
                SecondaryExternalIdAttributeId = dnAttr.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new() { AttributeId = dnAttr.Id, StringValue = groupDn }
                }
            },
            AttributeValueChanges = changes
        };
    }

    /// <summary>
    /// Creates a PendingExport for adding members to a groupOfNames that currently only has the placeholder member.
    /// The CSO shows the placeholder as the only current member value.
    /// </summary>
    private static PendingExport CreateGroupOfNamesMemberAddToPlaceholderGroupPendingExport(
        string groupDn,
        string[] membersToAdd,
        string placeholderDn = "cn=placeholder")
    {
        var csoType = new ConnectedSystemObjectType { Name = "groupOfNames" };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10, Name = "member", ConnectedSystemObjectType = csoType,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var changes = new List<PendingExportAttributeValueChange>();
        foreach (var member in membersToAdd)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = member
            });
        }

        var dnAttr = new ConnectedSystemObjectTypeAttribute
            { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType };

        // CSO has only the placeholder as the current member
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType,
                SecondaryExternalIdAttributeId = dnAttr.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new() { AttributeId = dnAttr.Id, StringValue = groupDn },
                    new()
                    {
                        AttributeId = memberAttr.Id,
                        Attribute = memberAttr,
                        UnresolvedReferenceValue = placeholderDn
                    }
                }
            },
            AttributeValueChanges = changes
        };
    }

    /// <summary>
    /// Creates a PendingExport for adding members to a groupOfNames that already has real members.
    /// </summary>
    private static PendingExport CreateGroupOfNamesMemberAddPendingExport(
        string groupDn,
        string[] membersToAdd,
        int currentMemberCount)
    {
        var csoType = new ConnectedSystemObjectType { Name = "groupOfNames" };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10, Name = "member", ConnectedSystemObjectType = csoType,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var changes = new List<PendingExportAttributeValueChange>();
        foreach (var member in membersToAdd)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = member
            });
        }

        var dnAttr = new ConnectedSystemObjectTypeAttribute
            { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType };

        var csoAttributeValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new() { AttributeId = dnAttr.Id, StringValue = groupDn }
        };

        for (var i = 0; i < currentMemberCount; i++)
        {
            csoAttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = memberAttr.Id,
                Attribute = memberAttr,
                UnresolvedReferenceValue = $"cn=existinguser{i:D4},ou=people,dc=test,dc=local"
            });
        }

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType,
                SecondaryExternalIdAttributeId = dnAttr.Id,
                AttributeValues = csoAttributeValues
            },
            AttributeValueChanges = changes
        };
    }

    private static DirectoryAttribute? GetDirectoryAttribute(AddRequest addRequest, string attributeName)
    {
        for (var i = 0; i < addRequest.Attributes.Count; i++)
        {
            if (addRequest.Attributes[i].Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                return addRequest.Attributes[i];
        }
        return null;
    }

    private static T CreateDirectoryResponse<T>(ResultCode resultCode) where T : DirectoryResponse
    {
        var response = (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: new object?[] { "", Array.Empty<DirectoryControl>(), resultCode, "", Array.Empty<Uri>() },
            culture: null)!;

        return response;
    }

    #endregion
}
