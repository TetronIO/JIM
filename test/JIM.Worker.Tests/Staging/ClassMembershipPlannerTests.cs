// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Staging;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Staging;

/// <summary>
/// What JIM writes into an object's class membership attribute, i.e. <c>objectClass</c> on an RFC 4512 directory.
/// </summary>
/// <remarks>
/// An administrator cannot flow this attribute, because no hand-written flow can know which auxiliary classes a
/// given object needs: that follows from which of the merged classes' attributes actually have values on this
/// object, which differs per object. JIM works it out instead, and these are the rules it works to.
/// </remarks>
[TestFixture]
public class ClassMembershipPlannerTests
{
    private const string ClassAttribute = "objectClass";

    #region Creates

    [Test]
    public void Plan_ForACreateWithNoAuxiliaryClasses_WritesJustTheStructuralClass()
    {
        var person = StructuralType();

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: [], AttributesBeingWritten("cn"), isCreate: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.AttributeName, Is.EqualTo(ClassAttribute));
            Assert.That(plan.ClassesToWrite, Is.EqualTo(new[] { "inetOrgPerson" }));
        }
    }

    [Test]
    public void Plan_ForACreateWritingAnAuxiliaryClassesAttribute_IncludesThatClass()
    {
        var person = StructuralTypeWithPosixAccountMerged();

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: [], AttributesBeingWritten("cn", "uidNumber"), isCreate: true);

        Assert.That(plan.ClassesToWrite, Is.EquivalentTo(new[] { "inetOrgPerson", "posixAccount" }));
    }

    /// <summary>
    /// Merging a class makes its attributes available; it does not say every object must carry the class. An object
    /// with no value for any of posixAccount's attributes is not a posixAccount, and saying it is would oblige it to
    /// satisfy that class's MUSTs for no reason.
    /// </summary>
    [Test]
    public void Plan_ForACreateWritingNoneOfAnAuxiliaryClassesAttributes_LeavesThatClassOut()
    {
        var person = StructuralTypeWithPosixAccountMerged();

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: [], AttributesBeingWritten("cn"), isCreate: true);

        Assert.That(plan.ClassesToWrite, Is.EqualTo(new[] { "inetOrgPerson" }));
    }

    /// <summary>
    /// An RFC 4512 entry must have exactly one structural class, so an object population identified by an auxiliary
    /// class cannot exist without one. The carrier an administrator named is what makes provisioning it possible.
    /// </summary>
    [Test]
    public void Plan_ForACreateOfAnAuxiliaryTypedObject_WritesTheCarrierAndTheAuxiliaryClass()
    {
        var posixAccount = AuxiliaryTypeWithCarrier();

        var plan = ClassMembershipPlanner.Plan(posixAccount, currentClasses: [], AttributesBeingWritten("uidNumber"), isCreate: true);

        Assert.That(plan.ClassesToWrite, Is.EqualTo(new[] { "account", "posixAccount" }),
            "the structural carrier has to come first: it is what the entry actually is");
    }

    #endregion

    #region Updates

    /// <summary>
    /// The delta convergence rule: an object gains an auxiliary class in the same change that first gives one of its
    /// attributes a value, rather than at some earlier point nobody asked for.
    /// </summary>
    [Test]
    public void Plan_ForAnUpdateWritingAnAuxiliaryAttributeTheObjectLacksTheClassFor_AddsThatClass()
    {
        var person = StructuralTypeWithPosixAccountMerged();

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: ["inetOrgPerson"], AttributesBeingWritten("uidNumber"), isCreate: false);

        Assert.That(plan.ClassesToWrite, Is.EqualTo(new[] { "posixAccount" }),
            "an update names what is being added, and restating a class the object already carries risks the directory rejecting the whole change");
    }

    [Test]
    public void Plan_ForAnUpdateOnAnObjectAlreadyCarryingTheClass_ChangesNothing()
    {
        var person = StructuralTypeWithPosixAccountMerged();

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: ["inetOrgPerson", "posixAccount"], AttributesBeingWritten("uidNumber"), isCreate: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.ClassesToWrite, Is.Empty);
            Assert.That(plan.HasChanges, Is.False);
        }
    }

    [Test]
    public void Plan_MatchesExistingClassesWithoutRegardToCase()
    {
        // LDAP descriptors are case-insensitive, and a directory may return a spelling other than the schema's.
        var person = StructuralTypeWithPosixAccountMerged();

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: ["INETORGPERSON", "POSIXACCOUNT"],
            AttributesBeingWritten("uidNumber"), isCreate: false);

        Assert.That(plan.ClassesToWrite, Is.Empty);
    }

    #endregion

    #region Required attributes

    /// <summary>
    /// Adding a class obliges the object to satisfy that class's requirements. Refusing here names the attributes an
    /// administrator has to flow; letting it through has the Connected System reject the change instead, with an
    /// error written in its own terms.
    /// </summary>
    [Test]
    public void Plan_WhenAddingAClassWhoseRequiredAttributeIsNotBeingWritten_ReportsIt()
    {
        var person = StructuralTypeWithPosixAccountMerged(requiredAuxiliaryAttributes: ["uidNumber", "gidNumber"]);

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: ["inetOrgPerson"], AttributesBeingWritten("uidNumber"), isCreate: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.MissingRequiredAttributes, Is.EqualTo(new[] { "gidNumber" }));
            Assert.That(plan.ClassesToWrite, Is.EqualTo(new[] { "posixAccount" }));
        }
    }

    [Test]
    public void Plan_WhenTheObjectAlreadyHasTheRequiredAttribute_DoesNotReportItMissing()
    {
        var person = StructuralTypeWithPosixAccountMerged(requiredAuxiliaryAttributes: ["uidNumber", "gidNumber"]);

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: ["inetOrgPerson"], AttributesBeingWritten("uidNumber"),
            isCreate: false, attributesAlreadyOnTheObject: ["gidNumber"]);

        Assert.That(plan.MissingRequiredAttributes, Is.Empty);
    }

    /// <summary>
    /// Only the classes being added are enforced. A class the object already carries is the Connected System's
    /// business, and JIM refusing an unrelated change over it would block work an administrator can do nothing about.
    /// </summary>
    [Test]
    public void Plan_DoesNotEnforceRequirementsOfAClassTheObjectAlreadyCarries()
    {
        var person = StructuralTypeWithPosixAccountMerged(requiredAuxiliaryAttributes: ["uidNumber", "gidNumber"]);

        var plan = ClassMembershipPlanner.Plan(person, currentClasses: ["inetOrgPerson", "posixAccount"],
            AttributesBeingWritten("uidNumber"), isCreate: false);

        Assert.That(plan.MissingRequiredAttributes, Is.Empty);
    }

    #endregion

    #region Connected Systems with no such concept

    [Test]
    public void Plan_ForAConnectedSystemThatDoesNotDeclareAClassAttribute_PlansNothing()
    {
        // A SQL table or a CSV file has no equivalent of objectClass, and must be left entirely alone.
        var table = new ConnectedSystemObjectType
        {
            Name = "Employees",
            Attributes = [Attribute("EmployeeId", null)]
        };

        var plan = ClassMembershipPlanner.Plan(table, currentClasses: [], AttributesBeingWritten("EmployeeId"), isCreate: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.AttributeName, Is.Null);
            Assert.That(plan.ClassesToWrite, Is.Empty);
            Assert.That(plan.HasChanges, Is.False);
        }
    }

    #endregion

    #region Fixtures

    private static ConnectedSystemObjectTypeAttribute Attribute(string name, string? className, bool required = false)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ClassName = className, Required = required, Type = AttributeDataType.Text
        };
    }

    private static List<string> AttributesBeingWritten(params string[] names) => [.. names];

    private static ConnectedSystemObjectType StructuralType()
    {
        var person = new ConnectedSystemObjectType
        {
            Name = "inetOrgPerson",
            Attributes = [Attribute("cn", "inetOrgPerson", required: true)]
        };

        person.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural });
        person.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = ClassAttribute });
        return person;
    }

    /// <summary>
    /// inetOrgPerson with posixAccount merged in, which is how the pairing looks after an administrator has selected
    /// it: posixAccount's attributes sit on the structural type carrying posixAccount's name.
    /// </summary>
    private static ConnectedSystemObjectType StructuralTypeWithPosixAccountMerged(string[]? requiredAuxiliaryAttributes = null)
    {
        var person = StructuralType();
        var required = requiredAuxiliaryAttributes ?? [];

        foreach (var name in new[] { "uidNumber", "gidNumber", "homeDirectory" })
            person.Attributes!.Add(Attribute(name, "posixAccount", required.Contains(name)));

        var posixAccount = new ConnectedSystemObjectType { Id = 2, Name = "posixAccount" };
        posixAccount.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary });

        person.Extensions.Add(new ConnectedSystemObjectTypeExtension { BaseObjectType = person, ExtensionObjectType = posixAccount });
        return person;
    }

    /// <summary>
    /// An object population identified by an auxiliary class, with the structural carrier an administrator named.
    /// </summary>
    private static ConnectedSystemObjectType AuxiliaryTypeWithCarrier()
    {
        var account = new ConnectedSystemObjectType { Id = 3, Name = "account" };
        account.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural });

        var posixAccount = new ConnectedSystemObjectType
        {
            Id = 4,
            Name = "posixAccount",
            Attributes = [Attribute("uidNumber", "posixAccount")],
            StructuralCarrierObjectType = account
        };

        posixAccount.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary });
        posixAccount.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = ClassAttribute });
        return posixAccount;
    }

    #endregion
}
