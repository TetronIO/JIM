// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.Models;
using NUnit.Framework;

namespace JIM.Worker.Tests.Models;

/// <summary>
/// The External ID values a Full Import keeps for deletion detection (#2521, Phase 3), compared as the External ID
/// attribute declares (#2051) whichever declaration is asked for, with nothing of the attributes kept.
/// </summary>
[TestFixture]
public class ImportedExternalIdsTests
{
    private const int UserType = 1;
    private const int GroupType = 2;

    [Test]
    public void ForObjectType_CaseExact_KeepsValuesDifferingOnlyInCaseApart()
    {
        var imported = new ImportedExternalIds();
        imported.Add(UserType, Text("JBloggs"));

        var values = imported.ForObjectType(UserType, caseExact: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values.Strings.Contains("JBloggs"), Is.True);
            Assert.That(values.Strings.Contains("jbloggs"), Is.False);
        }
    }

    [Test]
    public void ForObjectType_IgnoringCase_MatchesAValueInAnyCase()
    {
        var imported = new ImportedExternalIds();
        imported.Add(UserType, Text("JBloggs"));

        var values = imported.ForObjectType(UserType, caseExact: false);

        Assert.That(values.Contains("JBLOGGS", null, null, null, null), Is.True);
    }

    [Test]
    public void Add_ValuesDifferingOnlyInCase_KeepsBothSoACaseExactComparisonStillSeesEach()
    {
        var imported = new ImportedExternalIds();
        imported.Add(UserType, Text("JBloggs"));
        imported.Add(UserType, Text("jbloggs"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.ValueCount, Is.EqualTo(2));
            Assert.That(imported.ForObjectType(UserType, caseExact: true).Strings, Is.EquivalentTo(new[] { "JBloggs", "jbloggs" }));
        }
    }

    [Test]
    public void Add_EveryValueType_KeepsEachUnderItsObjectTypeOnly()
    {
        var anchor = Guid.NewGuid();
        var imported = new ImportedExternalIds();
        imported.Add(UserType, new ConnectedSystemImportObjectAttribute { Name = "n", Type = AttributeDataType.Number, IntValues = [7] });
        imported.Add(UserType, new ConnectedSystemImportObjectAttribute { Name = "l", Type = AttributeDataType.LongNumber, LongValues = [8L] });
        imported.Add(UserType, new ConnectedSystemImportObjectAttribute { Name = "d", Type = AttributeDataType.Decimal, DecimalValues = [4200.00m] });
        imported.Add(GroupType, new ConnectedSystemImportObjectAttribute { Name = "g", Type = AttributeDataType.Guid, GuidValues = [anchor] });

        var users = imported.ForObjectType(UserType, caseExact: false);
        var groups = imported.ForObjectType(GroupType, caseExact: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(users.Ints, Is.EquivalentTo(new[] { 7 }));
            Assert.That(users.Longs, Is.EquivalentTo(new[] { 8L }));
            Assert.That(users.Decimals.Contains(4200m), Is.True, "decimals compare numerically, whatever their scale");
            Assert.That(users.Guids, Is.Empty);
            Assert.That(groups.Guids, Is.EquivalentTo(new[] { anchor }));
            Assert.That(imported.ForObjectType(3, caseExact: false).Contains("x", 7, 8L, 4200m, anchor), Is.False, "nothing was read for an Object Type");
        }
    }

    [Test]
    public void Add_EmptyTextValue_IsNotKept()
    {
        var imported = new ImportedExternalIds();
        imported.Add(UserType, new ConnectedSystemImportObjectAttribute { Name = "id", Type = AttributeDataType.Text, StringValues = [""] });

        Assert.That(imported.ValueCount, Is.Zero);
    }

    [Test]
    public void Add_LaterChangeToTheAttribute_DoesNotChangeWhatWasKept()
    {
        // The values are copied out, so the attribute itself is not held (#2521).
        var attribute = Text("JBloggs");
        var imported = new ImportedExternalIds();
        imported.Add(UserType, attribute);

        attribute.StringValues.Clear();

        Assert.That(imported.ForObjectType(UserType, caseExact: true).Strings, Is.EquivalentTo(new[] { "JBloggs" }));
    }

    /// <summary>
    /// What 100,000 objects' External IDs cost to hold once their pages are gone: kept as the attributes the Connector
    /// returned (as the import kept them before #2521, Phase 3), and kept as values. Explicit, because a heap figure is
    /// read, not asserted. Each attribute's name is its own string, as a directory Connector reading each entry returns it.
    /// </summary>
    [TestCase(AttributeDataType.Guid)]
    [TestCase(AttributeDataType.Text)]
    [Explicit("A memory measurement for the bounded-memory Full Import work (#2521); read its output, nothing is asserted.")]
    public void Add_ManyObjects_MeasuresWhatIsHeldPerObject(AttributeDataType dataType)
    {
        const int objectCount = 100_000;

        var heldAsAttributes = HeapHeldBy(() => Attributes(dataType, objectCount).Select(attribute => (ObjectTypeId: UserType, Attribute: attribute)).ToList());
        var heldAsValues = HeapHeldBy(() =>
        {
            var values = new ImportedExternalIds();
            foreach (var attribute in Attributes(dataType, objectCount))
                values.Add(UserType, attribute);
            return values;
        });

        TestContext.Out.WriteLine($"{dataType}, {objectCount:N0} objects: as attributes {heldAsAttributes / (double)objectCount:N0} bytes an object " +
            $"({heldAsAttributes / 1024d / 1024d:N1} MB); as values {heldAsValues / (double)objectCount:N0} bytes an object ({heldAsValues / 1024d / 1024d:N1} MB)");
    }

    /// <summary>The managed heap what <paramref name="hold"/> builds keeps alive, after a forced collection either side.</summary>
    private static long HeapHeldBy(Func<object> hold)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var held = hold();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(held);
        return after - before;
    }

    private static IEnumerable<ConnectedSystemImportObjectAttribute> Attributes(AttributeDataType dataType, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var name = string.Concat("entry", "UUID");
            yield return dataType == AttributeDataType.Guid
                ? new ConnectedSystemImportObjectAttribute { Name = name, Type = dataType, GuidValues = [Guid.NewGuid()] }
                : new ConnectedSystemImportObjectAttribute { Name = name, Type = dataType, StringValues = [Guid.NewGuid().ToString()] };
        }
    }

    private static ConnectedSystemImportObjectAttribute Text(string value) =>
        new() { Name = "id", Type = AttributeDataType.Text, StringValues = [value] };
}
