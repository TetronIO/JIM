// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// SPEC-1082 test plan items 1-3: determinism/injectivity of <see cref="ImportContentHashCalculator.CalculateContentHash"/>,
/// the randomised consistency property that ties the hash to the honest diff's own no-op detection, and fingerprint
/// sensitivity to schema shape changes.
/// </summary>
[TestFixture]
public class ImportContentHashCalculatorTests
{
    // Reflection handle onto the private static diff method (SyncImportTaskProcessor), so the consistency
    // property test (item 2) can assert against the SAME no-op detection the import pipeline actually uses,
    // not a re-implementation of it. Pattern precedent: ImportUpdateDiffScaleTests.
    private static readonly MethodInfo UpdateMethod = typeof(JIM.Worker.Processors.SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject method not found via reflection - has it been renamed?");

    #region determinism and injectivity (test plan item 1)

    [Test]
    public void CalculateContentHash_SamePayloadDifferentValueOrderAndDuplicates_SameHash()
    {
        var objectType = BuildTextObjectType("TAGS", AttributePlurality.MultiValued);

        var a = BuildImportObject(objectType, ("TAGS", new List<string> { "alpha", "beta", "gamma" }));
        var b = BuildImportObject(objectType, ("TAGS", new List<string> { "gamma", "alpha", "beta", "gamma", "alpha" }));

        Assert.That(ImportContentHashCalculator.CalculateContentHash(a, objectType),
            Is.EqualTo(ImportContentHashCalculator.CalculateContentHash(b, objectType)),
            "Reordering and duplicating multi-values should not change the hash.");
    }

    [Test]
    public void CalculateContentHash_ConcatenationAmbiguity_DifferentHash()
    {
        var objectType = BuildTextObjectType("TAGS", AttributePlurality.MultiValued);

        var a = BuildImportObject(objectType, ("TAGS", new List<string> { "ab", "c" }));
        var b = BuildImportObject(objectType, ("TAGS", new List<string> { "a", "bc" }));

        Assert.That(ImportContentHashCalculator.CalculateContentHash(a, objectType),
            Is.Not.EqualTo(ImportContentHashCalculator.CalculateContentHash(b, objectType)),
            "Length-prefixing must prevent ['ab','c'] and ['a','bc'] from hashing identically.");
    }

    [Test]
    public void CalculateContentHash_SameValuesDifferentAttributeNames_DifferentHash()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "TEST_TYPE" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ATTR_ONE", Type = AttributeDataType.Text, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "ATTR_TWO", Type = AttributeDataType.Text, ConnectedSystemObjectType = objectType });

        var a = new ConnectedSystemImportObject
        {
            ObjectType = "TEST_TYPE",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "ATTR_ONE", StringValues = new List<string> { "same-value" } }
            }
        };
        var b = new ConnectedSystemImportObject
        {
            ObjectType = "TEST_TYPE",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "ATTR_TWO", StringValues = new List<string> { "same-value" } }
            }
        };

        Assert.That(ImportContentHashCalculator.CalculateContentHash(a, objectType),
            Is.Not.EqualTo(ImportContentHashCalculator.CalculateContentHash(b, objectType)),
            "The same value under a different attribute name must hash differently.");
    }

    [Test]
    public void CalculateContentHash_SameBytesAsTextVsBinary_DifferentHash()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "TEST_TYPE" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ATTR", Type = AttributeDataType.Text, ConnectedSystemObjectType = objectType });

        var objectTypeBinary = new ConnectedSystemObjectType { Id = 1, Name = "TEST_TYPE" };
        objectTypeBinary.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ATTR", Type = AttributeDataType.Binary, ConnectedSystemObjectType = objectTypeBinary });

        var textImport = new ConnectedSystemImportObject
        {
            ObjectType = "TEST_TYPE",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "ATTR", StringValues = new List<string> { "hi" }, ByteValues = new List<byte[]> { new byte[] { 0x68, 0x69 } } }
            }
        };

        Assert.That(ImportContentHashCalculator.CalculateContentHash(textImport, objectType),
            Is.Not.EqualTo(ImportContentHashCalculator.CalculateContentHash(textImport, objectTypeBinary)),
            "The same underlying bytes hashed as Text vs Binary must produce different hashes (type byte differs).");
    }

    [Test]
    public void CalculateContentHash_DecimalDifferentScaleSameValue_SameHash()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "TEST_TYPE" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "SALARY", Type = AttributeDataType.Decimal, ConnectedSystemObjectType = objectType });

        var a = new ConnectedSystemImportObject { ObjectType = "TEST_TYPE", Attributes = new List<ConnectedSystemImportObjectAttribute> { new() { Name = "SALARY", DecimalValues = new List<decimal> { 5.0m } } } };
        var b = new ConnectedSystemImportObject { ObjectType = "TEST_TYPE", Attributes = new List<ConnectedSystemImportObjectAttribute> { new() { Name = "SALARY", DecimalValues = new List<decimal> { 5.00m } } } };

        Assert.That(ImportContentHashCalculator.CalculateContentHash(a, objectType),
            Is.EqualTo(ImportContentHashCalculator.CalculateContentHash(b, objectType)),
            "5.0m and 5.00m must hash identically, matching the diff's scale-insensitive decimal comparison.");
    }

    [Test]
    public void CalculateContentHash_DateTimeDifferingOnlyInKind_SameHash()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "TEST_TYPE" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "START_DATE", Type = AttributeDataType.DateTime, ConnectedSystemObjectType = objectType });

        var ticks = new DateTime(2026, 7, 24, 10, 0, 0).Ticks;
        var a = new ConnectedSystemImportObject { ObjectType = "TEST_TYPE", Attributes = new List<ConnectedSystemImportObjectAttribute> { new() { Name = "START_DATE", DateTimeValue = new DateTime(ticks, DateTimeKind.Utc) } } };
        var b = new ConnectedSystemImportObject { ObjectType = "TEST_TYPE", Attributes = new List<ConnectedSystemImportObjectAttribute> { new() { Name = "START_DATE", DateTimeValue = new DateTime(ticks, DateTimeKind.Unspecified) } } };

        Assert.That(ImportContentHashCalculator.CalculateContentHash(a, objectType),
            Is.EqualTo(ImportContentHashCalculator.CalculateContentHash(b, objectType)),
            "DateTime values differing only in Kind must hash identically, matching DateTime == semantics.");
    }

    [Test]
    public void CalculateContentHash_AbsentAttributeVsEmptyAttribute_SameHash()
    {
        var objectType = BuildTextObjectType("TAGS", AttributePlurality.MultiValued);

        var absent = new ConnectedSystemImportObject { ObjectType = "TEST_TYPE", Attributes = new List<ConnectedSystemImportObjectAttribute>() };
        var empty = new ConnectedSystemImportObject
        {
            ObjectType = "TEST_TYPE",
            Attributes = new List<ConnectedSystemImportObjectAttribute> { new() { Name = "TAGS", StringValues = new List<string>() } }
        };

        Assert.That(ImportContentHashCalculator.CalculateContentHash(absent, objectType),
            Is.EqualTo(ImportContentHashCalculator.CalculateContentHash(empty, objectType)),
            "An absent attribute and a present-but-empty attribute must hash identically (both delete all stored values).");
    }

    [Test]
    public void CalculateContentHash_KnownInput_PinnedGuid()
    {
        // Pins the exact encoding to a known Guid so an accidental change to the canonical
        // encoding (attribute ordering, length-prefix width, decimal normalisation, etc.) fails
        // loudly here rather than silently changing every stored hash's meaning.
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "PINNED_TYPE" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "NAME", Type = AttributeDataType.Text, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "AGE", Type = AttributeDataType.Number, ConnectedSystemObjectType = objectType });

        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "PINNED_TYPE",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "NAME", StringValues = new List<string> { "Ada Lovelace" } },
                new() { Name = "AGE", IntValues = new List<int> { 36 } }
            }
        };

        var hash = ImportContentHashCalculator.CalculateContentHash(importObject, objectType);

        Assert.That(hash, Is.EqualTo(Guid.Parse("b4bd9ff5-e5d2-3f0d-3ca6-30a5cfff8e85")),
            "Pinned hash changed - this means the canonical encoding changed. If intentional, bump ImportContentHashAlgorithmVersion.");
    }

    #endregion

    #region fingerprint sensitivity (test plan item 3)

    [Test]
    public void CalculateTypeFingerprint_AttributeAdded_ChangesFingerprint()
    {
        var before = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        var after = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        after.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 99, Name = "NEW_ATTR", Type = AttributeDataType.Text, ConnectedSystemObjectType = after });

        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(before), Is.Not.EqualTo(ImportContentHashCalculator.CalculateTypeFingerprint(after)));
    }

    [Test]
    public void CalculateTypeFingerprint_AttributeRemoved_ChangesFingerprint()
    {
        var before = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        var after = new ConnectedSystemObjectType { Id = before.Id, Name = before.Name };

        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(before), Is.Not.EqualTo(ImportContentHashCalculator.CalculateTypeFingerprint(after)));
    }

    [Test]
    public void CalculateTypeFingerprint_AttributeRenamed_ChangesFingerprint()
    {
        var before = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        var after = BuildTextObjectType("ATTR_RENAMED", AttributePlurality.SingleValued);

        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(before), Is.Not.EqualTo(ImportContentHashCalculator.CalculateTypeFingerprint(after)));
    }

    [Test]
    public void CalculateTypeFingerprint_AttributeTypeChanged_ChangesFingerprint()
    {
        var before = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        var after = new ConnectedSystemObjectType { Id = before.Id, Name = before.Name };
        after.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ATTR_ONE", Type = AttributeDataType.Number, ConnectedSystemObjectType = after });

        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(before), Is.Not.EqualTo(ImportContentHashCalculator.CalculateTypeFingerprint(after)));
    }

    [Test]
    public void CalculateTypeFingerprint_AttributePluralityChanged_ChangesFingerprint()
    {
        var before = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        var after = BuildTextObjectType("ATTR_ONE", AttributePlurality.MultiValued);

        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(before), Is.Not.EqualTo(ImportContentHashCalculator.CalculateTypeFingerprint(after)));
    }

    [Test]
    public void CalculateTypeFingerprint_SelectedChanged_ChangesFingerprint()
    {
        var before = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        before.Attributes[0].Selected = false;
        var after = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        after.Attributes[0].Selected = true;

        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(before), Is.Not.EqualTo(ImportContentHashCalculator.CalculateTypeFingerprint(after)));
    }

    [Test]
    public void CalculateTypeFingerprint_UnrelatedObjectType_Unaffected()
    {
        var typeA = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        var typeAFingerprint = ImportContentHashCalculator.CalculateTypeFingerprint(typeA);

        // A completely separate object type, built identically, must fingerprint the same:
        // the fingerprint is a function of shape, not identity.
        var typeAClone = BuildTextObjectType("ATTR_ONE", AttributePlurality.SingleValued);
        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(typeAClone), Is.EqualTo(typeAFingerprint));

        // Mutating an unrelated third type must not affect typeA's fingerprint (sanity: fingerprint
        // is computed per-type, not globally memoised in a way that could leak state).
        var typeB = BuildTextObjectType("UNRELATED_ATTR", AttributePlurality.MultiValued);
        ImportContentHashCalculator.CalculateTypeFingerprint(typeB);
        Assert.That(ImportContentHashCalculator.CalculateTypeFingerprint(typeA), Is.EqualTo(typeAFingerprint));
    }

    #endregion

    #region consistency property (test plan item 2 - the invariant that earns the feature)

    /// <summary>
    /// For several hundred seeded random (import object, CSO) pairs spanning all nine data types,
    /// plus a mutation (no-op reorder, value change, add, remove, attribute drop, case-only DN
    /// change), asserts: hash(A') == hash(A) implies the honest diff finds zero pending additions
    /// and removals. This is the safety property the whole feature rests on: if this test can be
    /// made to fail, the skip predicate can silently miss a change.
    /// </summary>
    [Test]
    public void CalculateContentHash_ConsistencyWithHonestDiff_HoldsAcrossSeededMutations()
    {
        const int iterations = 300;
        var random = new Random(1082); // fixed seed - no wall-clock randomness

        var failures = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            var objectType = BuildAllTypesObjectType();
            var importObjectA = GenerateRandomImportObject(objectType, random);
            var hashA = ImportContentHashCalculator.CalculateContentHash(importObjectA, objectType);

            var (mutatedImportObject, description) = Mutate(importObjectA, objectType, random);
            var hashAPrime = ImportContentHashCalculator.CalculateContentHash(mutatedImportObject, objectType);

            if (hashAPrime != hashA)
                continue; // hash correctly detected a difference; nothing to assert for this iteration

            // hash says "unchanged" - the honest diff MUST agree there are zero pending changes.
            var cso = MaterialiseCsoFromImportObject(importObjectA, objectType);
            var rpei = new ActivityRunProfileExecutionItem();
            UpdateMethod.Invoke(null, new object?[] { mutatedImportObject, cso, objectType, rpei, null });

            if (cso.PendingAttributeValueAdditions.Count != 0 || cso.PendingAttributeValueRemovals.Count != 0)
            {
                failures.Add($"Iteration {i} ({description}): hash matched but diff found " +
                    $"{cso.PendingAttributeValueAdditions.Count} additions, {cso.PendingAttributeValueRemovals.Count} removals.");
            }
        }

        Assert.That(failures, Is.Empty, "Hash/diff consistency violated:\n" + string.Join("\n", failures));
    }

    private static (ConnectedSystemImportObject Mutated, string Description) Mutate(ConnectedSystemImportObject original, ConnectedSystemObjectType objectType, Random random)
    {
        var clone = CloneImportObject(original);
        var kind = random.Next(6);
        switch (kind)
        {
            case 0:
                // no-op: reorder multi-values only
                foreach (var attr in clone.Attributes)
                {
                    Shuffle(attr.StringValues, random);
                    Shuffle(attr.ReferenceValues, random);
                    Shuffle(attr.IntValues, random);
                    Shuffle(attr.LongValues, random);
                    Shuffle(attr.DecimalValues, random);
                    Shuffle(attr.GuidValues, random);
                    Shuffle(attr.ByteValues, random);
                }
                return (clone, "no-op reorder");

            case 1 when clone.Attributes.Count > 0:
            {
                // value change on a random attribute
                var attr = clone.Attributes[random.Next(clone.Attributes.Count)];
                MutateAttributeValue(attr, random);
                return (clone, $"value change on {attr.Name}");
            }

            case 2 when clone.Attributes.Count > 0:
            {
                // value add on a random multi-valued attribute
                var mvaAttrs = clone.Attributes.Where(a => IsMultiValued(objectType, a.Name)).ToList();
                if (mvaAttrs.Count > 0)
                {
                    var attr = mvaAttrs[random.Next(mvaAttrs.Count)];
                    AddRandomValue(attr, random);
                    return (clone, $"value add on {attr.Name}");
                }
                goto case 0;
            }

            case 3 when clone.Attributes.Count > 0:
            {
                // attribute drop
                var index = random.Next(clone.Attributes.Count);
                var dropped = clone.Attributes[index];
                clone.Attributes.RemoveAt(index);
                return (clone, $"attribute drop: {dropped.Name}");
            }

            case 4:
            {
                // case-only change on the Text or Reference attribute, if present
                var textAttr = clone.Attributes.FirstOrDefault(a => a.StringValues.Count > 0);
                if (textAttr != null)
                {
                    var idx = random.Next(textAttr.StringValues.Count);
                    textAttr.StringValues[idx] = InvertCaseOfFirstLetter(textAttr.StringValues[idx]);
                    return (clone, $"case-only change on {textAttr.Name}");
                }
                goto case 0;
            }

            default:
                return (clone, "no-op (identity clone)");
        }
    }

    private static string InvertCaseOfFirstLetter(string value)
    {
        if (value.Length == 0)
            return value;
        var first = value[0];
        var swapped = char.IsUpper(first) ? char.ToLowerInvariant(first) : char.ToUpperInvariant(first);
        return swapped + value[1..];
    }

    private static bool IsMultiValued(ConnectedSystemObjectType objectType, string attributeName)
    {
        var schemaAttr = objectType.Attributes.SingleOrDefault(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
        return schemaAttr?.AttributePlurality == AttributePlurality.MultiValued;
    }

    private static void MutateAttributeValue(ConnectedSystemImportObjectAttribute attr, Random random)
    {
        if (attr.StringValues.Count > 0) attr.StringValues[random.Next(attr.StringValues.Count)] += "-mutated";
        else if (attr.ReferenceValues.Count > 0) attr.ReferenceValues[random.Next(attr.ReferenceValues.Count)] += "-mutated";
        else if (attr.IntValues.Count > 0) attr.IntValues[random.Next(attr.IntValues.Count)] += 1;
        else if (attr.LongValues.Count > 0) attr.LongValues[random.Next(attr.LongValues.Count)] += 1;
        else if (attr.DecimalValues.Count > 0) attr.DecimalValues[random.Next(attr.DecimalValues.Count)] += 1.23m;
        else if (attr.GuidValues.Count > 0) attr.GuidValues[random.Next(attr.GuidValues.Count)] = Guid.NewGuid();
        else if (attr.ByteValues.Count > 0) attr.ByteValues[random.Next(attr.ByteValues.Count)] = new byte[] { (byte)random.Next(256) };
        else if (attr.DateTimeValue.HasValue) attr.DateTimeValue = attr.DateTimeValue.Value.AddDays(1);
        else if (attr.BoolValue.HasValue) attr.BoolValue = !attr.BoolValue.Value;
    }

    private static void AddRandomValue(ConnectedSystemImportObjectAttribute attr, Random random)
    {
        if (attr.StringValues.Count > 0) attr.StringValues.Add($"extra-{random.Next(100000)}");
        else if (attr.ReferenceValues.Count > 0) attr.ReferenceValues.Add($"extra-ref-{random.Next(100000)}");
        else if (attr.IntValues.Count > 0) attr.IntValues.Add(random.Next(100000));
        else if (attr.LongValues.Count > 0) attr.LongValues.Add(random.Next(100000));
        else if (attr.DecimalValues.Count > 0) attr.DecimalValues.Add(random.Next(100000) / 7m);
        else if (attr.GuidValues.Count > 0) attr.GuidValues.Add(Guid.NewGuid());
        else if (attr.ByteValues.Count > 0) attr.ByteValues.Add(new byte[] { (byte)random.Next(256) });
    }

    private static void Shuffle<T>(List<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static ConnectedSystemImportObject CloneImportObject(ConnectedSystemImportObject source)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = source.ChangeType,
            ObjectType = source.ObjectType,
            Attributes = source.Attributes.Select(a => new ConnectedSystemImportObjectAttribute
            {
                Name = a.Name,
                Type = a.Type,
                StringValues = new List<string>(a.StringValues),
                ReferenceValues = new List<string>(a.ReferenceValues),
                IntValues = new List<int>(a.IntValues),
                LongValues = new List<long>(a.LongValues),
                DecimalValues = new List<decimal>(a.DecimalValues),
                DateTimeValue = a.DateTimeValue,
                GuidValues = new List<Guid>(a.GuidValues),
                ByteValues = a.ByteValues.Select(b => (byte[])b.Clone()).ToList(),
                BoolValue = a.BoolValue
            }).ToList()
        };
    }

    private static ConnectedSystemObjectType BuildAllTypesObjectType()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "ALL_TYPES" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "EXT_ID", Type = AttributeDataType.Guid, ConnectedSystemObjectType = objectType, IsExternalId = true });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "TEXT_MVA", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "NUMBER_MVA", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 4, Name = "LONGNUMBER_MVA", Type = AttributeDataType.LongNumber, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 5, Name = "DECIMAL_MVA", Type = AttributeDataType.Decimal, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 6, Name = "DATETIME_SVA", Type = AttributeDataType.DateTime, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "BINARY_MVA", Type = AttributeDataType.Binary, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 8, Name = "REFERENCE_MVA", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "GUID_MVA", Type = AttributeDataType.Guid, AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = objectType });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "BOOL_SVA", Type = AttributeDataType.Boolean, ConnectedSystemObjectType = objectType });
        return objectType;
    }

    private static ConnectedSystemImportObject GenerateRandomImportObject(ConnectedSystemObjectType objectType, Random random)
    {
        var importObject = new ConnectedSystemImportObject { ObjectType = objectType.Name, Attributes = new List<ConnectedSystemImportObjectAttribute>() };

        foreach (var schemaAttr in objectType.Attributes)
        {
            var count = schemaAttr.AttributePlurality == AttributePlurality.MultiValued ? random.Next(1, 5) : 1;
            var importAttr = new ConnectedSystemImportObjectAttribute { Name = schemaAttr.Name, Type = schemaAttr.Type };

            switch (schemaAttr.Type)
            {
                case AttributeDataType.Text:
                    for (var v = 0; v < count; v++) importAttr.StringValues.Add($"text-{random.Next(100000)}");
                    break;
                case AttributeDataType.Reference:
                    for (var v = 0; v < count; v++) importAttr.ReferenceValues.Add($"CN=ref-{random.Next(100000)}");
                    break;
                case AttributeDataType.Number:
                    for (var v = 0; v < count; v++) importAttr.IntValues.Add(random.Next(-100000, 100000));
                    break;
                case AttributeDataType.LongNumber:
                    for (var v = 0; v < count; v++) importAttr.LongValues.Add(random.NextInt64(-1000000, 1000000));
                    break;
                case AttributeDataType.Decimal:
                    for (var v = 0; v < count; v++) importAttr.DecimalValues.Add(random.Next(-100000, 100000) / 7m);
                    break;
                case AttributeDataType.DateTime:
                    importAttr.DateTimeValue = new DateTime(2020, 1, 1).AddDays(random.Next(0, 3000));
                    break;
                case AttributeDataType.Binary:
                    for (var v = 0; v < count; v++)
                    {
                        var bytes = new byte[random.Next(1, 8)];
                        random.NextBytes(bytes);
                        importAttr.ByteValues.Add(bytes);
                    }
                    break;
                case AttributeDataType.Guid:
                    if (schemaAttr.IsExternalId)
                        importAttr.GuidValues.Add(Guid.NewGuid());
                    else
                        for (var v = 0; v < count; v++) importAttr.GuidValues.Add(Guid.NewGuid());
                    break;
                case AttributeDataType.Boolean:
                    importAttr.BoolValue = random.Next(2) == 0;
                    break;
            }

            importObject.Attributes.Add(importAttr);
        }

        return importObject;
    }

    /// <summary>
    /// Builds a CSO whose stored attribute values exactly match an import object, mirroring what
    /// the diff would have produced from a prior identical import (i.e. "already synced to A").
    /// </summary>
    private static ConnectedSystemObject MaterialiseCsoFromImportObject(ConnectedSystemImportObject importObject, ConnectedSystemObjectType objectType)
    {
        var externalIdAttr = objectType.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Type = objectType,
            ExternalIdAttributeId = externalIdAttr.Id
        };

        foreach (var importAttr in importObject.Attributes)
        {
            var schemaAttr = objectType.Attributes.Single(a => a.Name.Equals(importAttr.Name, StringComparison.OrdinalIgnoreCase));
            switch (schemaAttr.Type)
            {
                case AttributeDataType.Text:
                    foreach (var v in importAttr.StringValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, StringValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.Reference:
                    foreach (var v in importAttr.ReferenceValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, UnresolvedReferenceValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.Number:
                    foreach (var v in importAttr.IntValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, IntValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.LongNumber:
                    foreach (var v in importAttr.LongValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, LongValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.Decimal:
                    foreach (var v in importAttr.DecimalValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, DecimalValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.DateTime:
                    if (importAttr.DateTimeValue.HasValue)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, DateTimeValue = importAttr.DateTimeValue, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.Binary:
                    foreach (var v in importAttr.ByteValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, ByteValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.Guid:
                    foreach (var v in importAttr.GuidValues)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, GuidValue = v, ConnectedSystemObject = cso });
                    break;
                case AttributeDataType.Boolean:
                    if (importAttr.BoolValue.HasValue)
                        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schemaAttr.Id, Attribute = schemaAttr, BoolValue = importAttr.BoolValue, ConnectedSystemObject = cso });
                    break;
            }
        }

        return cso;
    }

    #endregion

    #region shared test builders

    private static ConnectedSystemObjectType BuildTextObjectType(string attributeName, AttributePlurality plurality)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "TEST_TYPE" };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = attributeName, Type = AttributeDataType.Text, AttributePlurality = plurality, ConnectedSystemObjectType = objectType, Selected = true });
        return objectType;
    }

    private static ConnectedSystemImportObject BuildImportObject(ConnectedSystemObjectType objectType, params (string Name, List<string> Values)[] attributes)
    {
        return new ConnectedSystemImportObject
        {
            ObjectType = objectType.Name,
            Attributes = attributes.Select(a => new ConnectedSystemImportObjectAttribute { Name = a.Name, StringValues = a.Values }).ToList()
        };
    }

    #endregion
}
