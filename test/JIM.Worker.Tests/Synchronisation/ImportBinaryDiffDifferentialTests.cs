// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// An import diffs a Binary attribute (<see cref="SyncImportTaskProcessor.StageBinaryValueChanges"/>) through a set of
/// each side's values keyed by their bytes (#2666) instead of scanning one side for each value of the other with
/// <c>AreByteArraysTheSame</c>. The sets must stage exactly what the scans staged, so these tests hold the two to one
/// outcome, value for value, in order and by reference, across a broad generated set of held and imported values:
/// equal bytes in distinct arrays and in the same array, empty arrays, null imported values and held rows holding no
/// bytes, duplicates on either side, and values of another attribute, including rows that name their attribute only
/// through the navigation or name none at all.
/// </summary>
[TestFixture]
public class ImportBinaryDiffDifferentialTests
{
    private const int Seed = 2666;
    private const int CaseCount = 20_000;
    private const int MinimumCasesPerFeature = 200;

    private static readonly ConnectedSystemObjectTypeAttribute Target = new()
    {
        Id = 24, Name = "certificates", Type = AttributeDataType.Binary, AttributePlurality = AttributePlurality.MultiValued
    };

    private static readonly ConnectedSystemObjectTypeAttribute Other = new()
    {
        Id = 10, Name = "photos", Type = AttributeDataType.Binary, AttributePlurality = AttributePlurality.MultiValued
    };

    // Contents the generator draws, each into an array of its own unless it reuses one already drawn: empty, one byte,
    // the same bytes in another order, a prefix of another, another with a trailing zero, and one certificate-sized.
    private static readonly byte[][] Patterns =
    [
        [], [0], [1, 2, 3], [3, 2, 1], [1, 2], [1, 2, 3, 0], [.. Enumerable.Range(0, 256).Select(b => (byte)b)]
    ];

    private enum Feature
    {
        HeldDuplicate,
        ImportDuplicate,
        EqualBytesInDistinctArrays,
        SameArrayOnBothSides,
        HeldEmpty,
        ImportEmpty,
        ImportNull,
        HeldWithoutBytes,
        OtherAttributeHoldsImportedBytes,
        AttributeNamedOnlyByNavigation,
        AttributeNamedNowhere,
        ValuesRemoved,
        ValuesAdded,
        NothingStaged
    }

    [Test]
    public void StageBinaryValueChanges_SetsAndScans_StageTheSameValuesInTheSameOrderForEveryGeneratedCase()
    {
        var random = new Random(Seed);
        var disagreements = new List<string>();
        var features = Enum.GetValues<Feature>().ToDictionary(feature => feature, _ => 0);

        for (var i = 0; i < CaseCount; i++)
        {
            var (cso, importedValues, caseFeatures) = GenerateCase(random);
            var (expectedRemovals, expectedAdditions) = ScanReference(cso, Target, importedValues);
            var removalsBefore = cso.PendingAttributeValueRemovals.Count;

            SyncImportTaskProcessor.StageBinaryValueChanges(cso, Target, importedValues);

            var actualRemovals = cso.PendingAttributeValueRemovals.Skip(removalsBefore).ToList();
            var actualAdditions = cso.PendingAttributeValueAdditions;
            if (!actualRemovals.SequenceEqual(expectedRemovals, ReferenceEqualityComparer.Instance))
                disagreements.Add($"case {i}: removed [{Describe(actualRemovals.Select(av => av.ByteValue))}] where the scan removed [{Describe(expectedRemovals.Select(av => av.ByteValue))}]");
            if (!actualAdditions.Select(av => av.ByteValue).SequenceEqual(expectedAdditions, ReferenceEqualityComparer.Instance) ||
                actualAdditions.Any(av => !ReferenceEquals(av.Attribute, Target) || !ReferenceEquals(av.ConnectedSystemObject, cso)))
                disagreements.Add($"case {i}: added [{Describe(actualAdditions.Select(av => av.ByteValue))}] where the scan added [{Describe(expectedAdditions)}]");

            foreach (var feature in caseFeatures)
                features[feature]++;
            if (expectedRemovals.Count > 0)
                features[Feature.ValuesRemoved]++;
            if (expectedAdditions.Count > 0)
                features[Feature.ValuesAdded]++;
            if (expectedRemovals.Count == 0 && expectedAdditions.Count == 0)
                features[Feature.NothingStaged]++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreements, Is.Empty, string.Join(Environment.NewLine, disagreements.Take(20)));
            foreach (var (feature, count) in features)
                Assert.That(count, Is.GreaterThanOrEqualTo(MinimumCasesPerFeature), $"the generator produced too few cases with {feature}");
        }
    }

    /// <summary>
    /// The import's Binary diff as it stood before #2666, kept as written: each held row of the attribute holding bytes
    /// is removed if no imported value has the same bytes, and each imported value is added if no such held row has its
    /// bytes, with byte arrays compared by <c>AreByteArraysTheSame</c>.
    /// </summary>
    private static (List<ConnectedSystemObjectAttributeValue> Removals, List<byte[]> Additions) ScanReference(
        ConnectedSystemObject connectedSystemObject, ConnectedSystemObjectTypeAttribute csoAttribute, List<byte[]> importedByteValues)
    {
        var removals = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.ByteValue != null && !importedByteValues.Any(i => JIM.Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue))).ToList();
        var additions = importedByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.ByteValue != null && JIM.Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue))).ToList();
        return (removals, additions);
    }

    private static (ConnectedSystemObject Cso, List<byte[]> ImportedValues, List<Feature> Features) GenerateCase(Random random)
    {
        var features = new List<Feature>();
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var drawn = new List<byte[]>();

        var heldCount = random.Next(5) == 0 ? random.Next(30, 60) : random.Next(0, 8);
        for (var i = 0; i < heldCount; i++)
        {
            var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso };
            NameAttribute(random, value, random.Next(5) == 0 ? Other : Target, features);

            // Now and then a row holding no bytes (a value held under another type).
            if (random.Next(8) == 0)
                value.StringValue = "held as text";
            else
                value.ByteValue = Draw(random, drawn);
            cso.AttributeValues.Add(value);
        }

        var importedValues = Enumerable.Range(0, random.Next(0, heldCount > 10 ? 60 : 8))
            .Select(_ => random.Next(10) == 0 ? null! : Draw(random, drawn))
            .ToList();

        var heldBytes = cso.AttributeValues.Where(av => HoldsTarget(av) && av.ByteValue != null).Select(av => av.ByteValue!).ToList();
        var importedBytes = importedValues.Where(value => value != null).ToList();
        if (HasDuplicateContent(heldBytes))
            features.Add(Feature.HeldDuplicate);
        if (HasDuplicateContent(importedBytes))
            features.Add(Feature.ImportDuplicate);
        if (heldBytes.Any(held => importedBytes.Any(imported => !ReferenceEquals(held, imported) && held.AsSpan().SequenceEqual(imported))))
            features.Add(Feature.EqualBytesInDistinctArrays);
        if (heldBytes.Any(held => importedBytes.Any(imported => ReferenceEquals(held, imported))))
            features.Add(Feature.SameArrayOnBothSides);
        if (heldBytes.Any(held => held.Length == 0))
            features.Add(Feature.HeldEmpty);
        if (importedBytes.Any(imported => imported.Length == 0))
            features.Add(Feature.ImportEmpty);
        if (importedValues.Contains(null!))
            features.Add(Feature.ImportNull);
        if (cso.AttributeValues.Any(av => HoldsTarget(av) && av.ByteValue == null))
            features.Add(Feature.HeldWithoutBytes);
        if (cso.AttributeValues.Any(av => !HoldsTarget(av) && av.ByteValue != null && importedBytes.Any(imported => imported.AsSpan().SequenceEqual(av.ByteValue))))
            features.Add(Feature.OtherAttributeHoldsImportedBytes);

        return (cso, importedValues, features);
    }

    // A row names its attribute by id, by the navigation alone (an id of 0, as a row not yet saved has), by an id the
    // navigation contradicts (the id wins), or not at all.
    private static void NameAttribute(Random random, ConnectedSystemObjectAttributeValue value, ConnectedSystemObjectTypeAttribute attribute, List<Feature> features)
    {
        switch (random.Next(6))
        {
            case 0:
                value.Attribute = attribute;
                features.Add(Feature.AttributeNamedOnlyByNavigation);
                break;
            case 1:
                value.AttributeId = attribute.Id;
                value.Attribute = ReferenceEquals(attribute, Target) ? Other : Target;
                break;
            case 2:
                value.Attribute = null!;
                features.Add(Feature.AttributeNamedNowhere);
                break;
            default:
                value.AttributeId = attribute.Id;
                value.Attribute = attribute;
                break;
        }
    }

    private static bool HoldsTarget(ConnectedSystemObjectAttributeValue value) =>
        (value.AttributeId != 0 ? value.AttributeId : value.Attribute?.Id) == Target.Id;

    // An array already drawn in this case, now and then, or a fresh copy of a pattern.
    private static byte[] Draw(Random random, List<byte[]> drawn)
    {
        if (drawn.Count > 0 && random.Next(6) == 0)
            return drawn[random.Next(drawn.Count)];

        var value = (byte[])Patterns[random.Next(Patterns.Length)].Clone();
        drawn.Add(value);
        return value;
    }

    private static bool HasDuplicateContent(List<byte[]> values) =>
        values.Select(value => Convert.ToHexString(value)).GroupBy(hex => hex).Any(group => group.Count() > 1);

    private static string Describe(IEnumerable<byte[]?> values) =>
        string.Join(", ", values.Select(value => value == null ? "null" : $"0x{Convert.ToHexString(value)}#{value.GetHashCode()}"));
}
