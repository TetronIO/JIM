// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Staging;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// On an RFC 4512 directory an auxiliary class attaches to an entry rather than to the schema, so nothing in the
/// directory says that a person entry carries posixAccount's attributes; an administrator does, by selecting the
/// auxiliary class. This merger is what turns that selection into attributes on the structural Object Type, which is
/// what a Synchronisation Rule can then map.
/// </summary>
/// <remarks>
/// It reconciles rather than only adds, so that it gives the same answer whether it runs after a schema refresh or
/// straight after an administrator changes their selection. An auxiliary class's contribution is recognised by the
/// attribute's ClassName: discovery stamps every attribute with the class the Object Type was built from, so an
/// attribute on a structural type bearing an auxiliary class's name can only have got there through a merge.
/// </remarks>
[TestFixture]
public class AuxiliaryClassAttributeMergerTests
{
    private const int PersonId = 1;
    private const int PosixAccountId = 2;
    private const int ShadowAccountId = 3;

    private static ConnectedSystemObjectTypeAttribute Attribute(string name, string className, bool selected = false)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Name = name,
            ClassName = className,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Writability = AttributeWritability.Writable,
            Selected = selected
        };
    }

    private static ConnectedSystemObjectType ObjectType(int id, string name, string classKind, params ConnectedSystemObjectTypeAttribute[] attributes)
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = id,
            Name = name,
            Attributes = attributes.ToList()
        };

        objectType.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = classKind });
        return objectType;
    }

    /// <summary>
    /// A person structural type and two auxiliary types, with no selections made yet.
    /// </summary>
    private static ConnectedSystem BuildSystem()
    {
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            ObjectTypes =
            [
                ObjectType(PersonId, "person", ObjectTypeTags.Values.ClassKindStructural,
                    Attribute("cn", "person"), Attribute("sn", "person")),
                ObjectType(PosixAccountId, "posixAccount", ObjectTypeTags.Values.ClassKindAuxiliary,
                    Attribute("uid", "posixAccount"), Attribute("uidNumber", "posixAccount"), Attribute("cn", "posixAccount")),
                ObjectType(ShadowAccountId, "shadowAccount", ObjectTypeTags.Values.ClassKindAuxiliary,
                    Attribute("uid", "shadowAccount"), Attribute("shadowLastChange", "shadowAccount"))
            ]
        };
    }

    private static void Extend(ConnectedSystem connectedSystem, int baseId, int extensionId)
    {
        var baseType = connectedSystem.ObjectTypes!.Single(ot => ot.Id == baseId);
        var extensionType = connectedSystem.ObjectTypes!.Single(ot => ot.Id == extensionId);

        baseType.Extensions.Add(new ConnectedSystemObjectTypeExtension
        {
            BaseObjectType = baseType,
            BaseObjectTypeId = baseId,
            ExtensionObjectType = extensionType,
            ExtensionObjectTypeId = extensionId
        });
    }

    private static ConnectedSystemObjectType Person(ConnectedSystem connectedSystem)
    {
        return connectedSystem.ObjectTypes!.Single(ot => ot.Id == PersonId);
    }

    [Test]
    public void Merge_WithASelectedAuxiliaryClass_AddsItsAttributesToTheStructuralType()
    {
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        Assert.That(Person(connectedSystem).Attributes!.Select(a => a.Name),
            Is.EquivalentTo(new[] { "cn", "sn", "uid", "uidNumber" }));
    }

    [Test]
    public void Merge_WithASelectedAuxiliaryClass_RecordsWhichClassEachAttributeCameFrom()
    {
        // Provenance is what lets the portal say why an attribute is on this type, and is how the merger later
        // recognises its own work. Without it a merged attribute is indistinguishable from a native one.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        Assert.That(Person(connectedSystem).Attributes!.Single(a => a.Name == "uidNumber").ClassName,
            Is.EqualTo("posixAccount"));
    }

    [Test]
    public void Merge_WhereTheStructuralTypeAlreadyHasTheAttribute_LeavesTheStructuralOneAlone()
    {
        // posixAccount also declares cn. The structural type's own attribute must survive untouched: its Id is what
        // Synchronisation Rule mappings reference, and its Selected state is the administrator's.
        var connectedSystem = BuildSystem();
        Person(connectedSystem).Attributes!.Single(a => a.Name == "cn").Selected = true;
        Extend(connectedSystem, PersonId, PosixAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        var cn = Person(connectedSystem).Attributes!.Where(a => a.Name == "cn").ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cn, Has.Count.EqualTo(1), "an attribute both classes declare is still one attribute");
            Assert.That(cn[0].ClassName, Is.EqualTo("person"));
            Assert.That(cn[0].Selected, Is.True);
        }
    }

    [Test]
    public void Merge_WithTwoAuxiliaryClassesDeclaringTheSameAttribute_AddsItOnce()
    {
        // Both posixAccount and shadowAccount declare uid. Two rows of the same name on one Object Type would be
        // ambiguous everywhere downstream.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);
        Extend(connectedSystem, PersonId, ShadowAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        Assert.That(Person(connectedSystem).Attributes!.Count(a => a.Name == "uid"), Is.EqualTo(1));
    }

    [Test]
    public void Merge_LeavesMergedAttributesUnselected()
    {
        // Selecting an auxiliary class says its attributes are available, not that JIM should manage all of them.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        Assert.That(Person(connectedSystem).Attributes!.Single(a => a.Name == "uidNumber").Selected, Is.False);
    }

    [Test]
    public void Merge_RunTwice_ChangesNothingTheSecondTime()
    {
        // The merger runs on every schema refresh as well as on selection changes, so a second pass must be a no-op
        // rather than a second copy of everything.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);
        var afterFirst = Person(connectedSystem).Attributes!.Select(a => a.Name).OrderBy(n => n).ToList();
        var second = AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Person(connectedSystem).Attributes!.Select(a => a.Name).OrderBy(n => n), Is.EqualTo(afterFirst));
            Assert.That(second.AddedAttributes, Is.Empty);
            Assert.That(second.RemovedAttributes, Is.Empty);
        }
    }

    [Test]
    public void Merge_AfterAnAuxiliaryClassIsDeselected_TakesItsAttributesBackOff()
    {
        // Otherwise a deselected class would leave attributes behind that no class contributes, which an
        // administrator could still map and JIM could never write.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);
        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        Person(connectedSystem).Extensions.Clear();
        var result = AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Person(connectedSystem).Attributes!.Select(a => a.Name), Is.EquivalentTo(new[] { "cn", "sn" }));
            Assert.That(result.RemovedAttributes["person"], Is.EquivalentTo(new[] { "uid", "uidNumber" }));
        }
    }

    [Test]
    public void Merge_DoesNotTouchTheAuxiliaryTypesOwnAttributes()
    {
        // The auxiliary Object Type is still a type in its own right; merging copies from it rather than moving.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);

        AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        Assert.That(connectedSystem.ObjectTypes!.Single(ot => ot.Id == PosixAccountId).Attributes!.Select(a => a.Name),
            Is.EquivalentTo(new[] { "uid", "uidNumber", "cn" }));
    }

    [Test]
    public void Merge_WithNoSelectionsAtAll_ChangesNothing()
    {
        var connectedSystem = BuildSystem();

        var result = AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Person(connectedSystem).Attributes!.Select(a => a.Name), Is.EquivalentTo(new[] { "cn", "sn" }));
            Assert.That(result.AddedAttributes, Is.Empty);
        }
    }

    [Test]
    public void Merge_WhereTheAuxiliaryTypeIsNoLongerInTheSchema_ReportsItRatherThanFailing()
    {
        // A refresh can remove an auxiliary class the directory no longer publishes. The database cascade takes the
        // selection with it, but the merge runs on the in-memory graph first and must survive the gap and say so.
        var connectedSystem = BuildSystem();
        Extend(connectedSystem, PersonId, PosixAccountId);
        connectedSystem.ObjectTypes!.RemoveAll(ot => ot.Id == PosixAccountId);

        var result = AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UnresolvedExtensions, Has.Count.EqualTo(1));
            Assert.That(Person(connectedSystem).Attributes!.Select(a => a.Name), Is.EquivalentTo(new[] { "cn", "sn" }));
        }
    }

    [Test]
    public void Merge_WithNoObjectTypesAtAll_DoesNotThrow()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Directory" };

        Assert.That(() => AuxiliaryClassAttributeMerger.Merge(connectedSystem), Throws.Nothing);
    }
}
