// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What the LDAP Connector puts in an add request's <c>objectClass</c>.
/// </summary>
/// <remarks>
/// JIM works out which classes an entry belongs to and states them on the Pending Export; the Connector's job is to
/// send them, all of them, in one multi-valued attribute. An entry created with only its structural class would
/// reject the very auxiliary attributes the same add is carrying.
/// </remarks>
[TestFixture]
public class LdapConnectorExportObjectClassTests
{
    private LdapConnectorExport CreateExport()
    {
        return new LdapConnectorExport(new Mock<ILdapOperationExecutor>().Object, [], Log.Logger, 1);
    }

    [Test]
    public void BuildAddRequest_WithEveryClassJimStated_SendsThemAllInOneAttribute()
    {
        var pendingExport = PendingExportWithClasses("inetOrgPerson", "posixAccount");

        var (addRequest, _) = CreateExport().BuildAddRequestWithOverflow(pendingExport, "uid=jbloggs,ou=People,dc=example,dc=org");

        var objectClass = ObjectClassValues(addRequest);
        Assert.That(objectClass, Is.EqualTo(new[] { "inetOrgPerson", "posixAccount" }),
            "the structural class comes first, and every auxiliary class the entry belongs to follows it");
    }

    [Test]
    public void BuildAddRequest_WithOneClass_SendsJustThatOne()
    {
        var pendingExport = PendingExportWithClasses("inetOrgPerson");

        var (addRequest, _) = CreateExport().BuildAddRequestWithOverflow(pendingExport, "uid=jbloggs,ou=People,dc=example,dc=org");

        Assert.That(ObjectClassValues(addRequest), Is.EqualTo(new[] { "inetOrgPerson" }));
    }

    [Test]
    public void BuildAddRequest_IgnoresARepeatedClass()
    {
        // Belt and braces: a duplicated value is a constraint violation at most directories, and the entry would
        // fail to create over something JIM could simply not have sent.
        var pendingExport = PendingExportWithClasses("inetOrgPerson", "posixAccount", "POSIXACCOUNT");

        var (addRequest, _) = CreateExport().BuildAddRequestWithOverflow(pendingExport, "uid=jbloggs,ou=People,dc=example,dc=org");

        Assert.That(ObjectClassValues(addRequest), Is.EqualTo(new[] { "inetOrgPerson", "posixAccount" }));
    }

    /// <summary>
    /// A Connected System configured before JIM computed class membership, or one whose schema has not been
    /// refreshed since, has no stated classes on its Pending Exports. It must go on provisioning exactly as it did.
    /// </summary>
    [Test]
    public void BuildAddRequest_WithNoClassStated_FallsBackToTheObjectTypesName()
    {
        var objectType = new ConnectedSystemObjectType { Name = "inetOrgPerson" };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Type = objectType },
            AttributeValueChanges =
            [
                Change(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "cn", ConnectedSystemObjectType = objectType }, "Joe Bloggs")
            ]
        };

        var (addRequest, _) = CreateExport().BuildAddRequestWithOverflow(pendingExport, "uid=jbloggs,ou=People,dc=example,dc=org");

        Assert.That(ObjectClassValues(addRequest), Is.EqualTo(new[] { "inetOrgPerson" }));
    }

    /// <summary>
    /// The auxiliary class's attributes must travel with the class that permits them, in the same add.
    /// </summary>
    [Test]
    public void BuildAddRequest_SendsTheAuxiliaryClassesAttributesAlongsideIt()
    {
        var pendingExport = PendingExportWithClasses("inetOrgPerson", "posixAccount");
        var objectType = pendingExport.ConnectedSystemObject!.Type;
        pendingExport.AttributeValueChanges.Add(
            Change(new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "uidNumber", ClassName = "posixAccount", ConnectedSystemObjectType = objectType }, "5001"));

        var (addRequest, _) = CreateExport().BuildAddRequestWithOverflow(pendingExport, "uid=jbloggs,ou=People,dc=example,dc=org");

        var uidNumber = addRequest.Attributes.Cast<DirectoryAttribute>().SingleOrDefault(a => a.Name == "uidNumber");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ObjectClassValues(addRequest), Contains.Item("posixAccount"));
            Assert.That(uidNumber, Is.Not.Null);
            Assert.That(uidNumber!.GetValues(typeof(string)).Cast<string>().Single(), Is.EqualTo("5001"));
        }
    }

    /// <summary>
    /// Delta convergence: an entry gains an auxiliary class in the same modify that first flows one of its
    /// attributes, as an add rather than a replace. Replacing would restate the entry's whole class membership from
    /// what JIM believes it to be, and JIM's belief is only as fresh as its last import.
    /// </summary>
    [Test]
    public void BuildModifyRequests_ForAClassBeingAdded_SendsItAsAnAddNotAReplace()
    {
        var pendingExport = PendingExportWithClasses("posixAccount");
        pendingExport.ChangeType = PendingExportChangeType.Update;

        var modifications = LdapConnectorExport.ConsolidateModifications(pendingExport, "uid=jbloggs,ou=People,dc=example,dc=org");

        var objectClass = modifications.Single(m => m.AttributeName == "objectClass");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectClass.Operation, Is.EqualTo(DirectoryAttributeOperation.Add));
            Assert.That(objectClass.AttributeChanges.Select(change => change.StringValue), Is.EqualTo(new[] { "posixAccount" }));
        }
    }

    #region Fixtures

    private static string[] ObjectClassValues(AddRequest addRequest)
    {
        var attribute = addRequest.Attributes.Cast<DirectoryAttribute>().Single(a => a.Name == "objectClass");
        return attribute.GetValues(typeof(string)).Cast<string>().ToArray();
    }

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectTypeAttribute attribute, string value)
    {
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value,
            ChangeType = PendingExportAttributeChangeType.Add
        };
    }

    private static PendingExport PendingExportWithClasses(params string[] classNames)
    {
        var objectType = new ConnectedSystemObjectType { Name = "inetOrgPerson" };
        var objectClassAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "objectClass",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Type = objectType }
        };

        foreach (var className in classNames)
            pendingExport.AttributeValueChanges.Add(Change(objectClassAttribute, className));

        return pendingExport;
    }

    #endregion
}
