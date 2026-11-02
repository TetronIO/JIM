// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The Text writer of inbound Attribute Flow (<see cref="SyncEngine.ProcessTextAttribute"/>) diffs the source's values
/// against the Metaverse Object's through an ordinal set of each side (#2666) instead of scanning one side for each
/// value of the other. The sets must stage exactly what the scans staged, so these tests hold the two to one outcome,
/// value for value and in order, across a broad generated set of held and source values: text differing only in case,
/// in leading or trailing whitespace, or in how an accent is encoded; empty text; held rows holding no text; duplicates
/// on either side; held values already pending removal; and values of another attribute holding the same text.
/// </summary>
[TestFixture]
public class TextAttributeFlowDifferentialTests
{
    private const int Seed = 2666;
    private const int CaseCount = 20_000;
    private const int MinimumCasesPerFeature = 200;
    private const int TargetAttributeId = 100;
    private const int OtherAttributeId = 101;
    private const int ConnectedSystemId = 5;
    private const int SyncRuleId = 7;

    // Text the generator draws for both sides: case variants, whitespace variants, empty and blank text, the Turkish
    // capital dotted I beside its lower-case forms, and an accent precomposed beside the same accent combining, which
    // only an ordinal comparison tells apart from each other.
    private static readonly string[] Texts =
    [
        "alpha", "Alpha", "ALPHA", " alpha", "alpha ", "\talpha", "", " ", "beta", "İnci", "inci", "INCI", "café", "café"
    ];

    private static readonly MetaverseAttribute Target = new()
    {
        Id = TargetAttributeId, Name = "Mail Aliases", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
    };

    private static readonly MetaverseAttribute Other = new()
    {
        Id = OtherAttributeId, Name = "Other Aliases", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
    };

    private enum Feature
    {
        HeldDuplicate,
        SourceDuplicate,
        HeldWithoutText,
        HeldEmptyText,
        SourceEmptyText,
        HeldAlreadyPendingRemoval,
        OtherAttributeHoldsSourceText,
        ValuesRemoved,
        ValuesAdded,
        NothingStaged
    }

    [Test]
    public void ProcessTextAttribute_SetsAndScans_StageTheSameValuesInTheSameOrderForEveryGeneratedCase()
    {
        var random = new Random(Seed);
        var mapping = new SyncRuleMapping { Id = SyncRuleId, SyncRuleId = SyncRuleId, TargetMetaverseAttribute = Target, Priority = 1 };
        var disagreements = new List<string>();
        var features = Enum.GetValues<Feature>().ToDictionary(feature => feature, _ => 0);

        for (var i = 0; i < CaseCount; i++)
        {
            var (mvo, sourceValues, caseFeatures) = GenerateCase(random);
            var (expectedRemovals, expectedAdditions) = ScanReference(mvo, sourceValues);
            var removalsBefore = mvo.PendingAttributeValueRemovals.Count;
            var additionsBefore = mvo.PendingAttributeValueAdditions.Count;

            SyncEngine.ProcessTextAttribute(mvo, mapping, sourceValues, ConnectedSystemId, SyncRuleId);

            var actualRemovals = mvo.PendingAttributeValueRemovals.Skip(removalsBefore).ToList();
            var actualAdditions = mvo.PendingAttributeValueAdditions.Skip(additionsBefore).ToList();
            if (!actualRemovals.SequenceEqual(expectedRemovals, ReferenceEqualityComparer.Instance))
                disagreements.Add($"case {i}: removed [{Describe(actualRemovals)}] where the scan removed [{Describe(expectedRemovals)}]");
            if (!actualAdditions.Select(av => av.StringValue).SequenceEqual(expectedAdditions, StringComparer.Ordinal) ||
                actualAdditions.Any(av => av.AttributeId != TargetAttributeId || !ReferenceEquals(av.Attribute, Target)))
                disagreements.Add($"case {i}: added [{Describe(actualAdditions)}] where the scan added [{string.Join(", ", expectedAdditions.Select(Quote))}]");

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
    /// The writer's diff as it stood before #2666, kept as written: each held value is removed if no source value equals
    /// its text ordinally, and each source value is added if no held value's text equals it. It reads the held values
    /// once, as the writer does, before anything is staged.
    /// </summary>
    private static (List<MetaverseObjectAttributeValue> Removals, List<string> Additions) ScanReference(MetaverseObject mvo, List<string> effectiveValues)
    {
        var currentValues = SyncEngine.GetEffectiveAttributeValues(mvo, TargetAttributeId).ToList();
        var removals = currentValues.Where(mvoav =>
            !effectiveValues.Any(ev => ev.Equals(mvoav.StringValue, StringComparison.Ordinal))).ToList();
        var additions = effectiveValues.Where(ev =>
            !currentValues.Any(mvoav => ev.Equals(mvoav.StringValue, StringComparison.Ordinal))).ToList();
        return (removals, additions);
    }

    private static (MetaverseObject Mvo, List<string> SourceValues, List<Feature> Features) GenerateCase(Random random)
    {
        var features = new List<Feature>();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        // Mostly a handful of values; now and then enough that the pending removals index themselves.
        var heldCount = random.Next(5) == 0 ? random.Next(30, 60) : random.Next(0, 8);
        for (var i = 0; i < heldCount; i++)
        {
            // Now and then a row of the attribute holding no text at all (a value held under another type).
            var text = random.Next(8) == 0 ? null : Texts[random.Next(Texts.Length)];
            mvo.AttributeValues.Add(HeldValue(mvo, Target, text));
        }

        // A value of another attribute holding text the source may hold too.
        if (random.Next(2) == 0)
            mvo.AttributeValues.Add(HeldValue(mvo, Other, Texts[random.Next(Texts.Length)]));

        // Some held values already pending removal this run, as a departing contributor's recall leaves them.
        foreach (var held in mvo.AttributeValues.Where(_ => random.Next(6) == 0))
        {
            mvo.PendingAttributeValueRemovals.Add(held);
            if (held.AttributeId == TargetAttributeId)
                features.Add(Feature.HeldAlreadyPendingRemoval);
        }

        // Mostly a source drawn afresh; now and then the held text handed back reordered, perhaps with one value changed,
        // as an unchanged or barely changed attribute's synchronisation does.
        List<string> sourceValues;
        var heldTextValues = mvo.AttributeValues.Where(av => av.AttributeId == TargetAttributeId && av.StringValue != null).Select(av => av.StringValue!).ToList();
        if (heldTextValues.Count > 0 && random.Next(3) == 0)
        {
            sourceValues = [.. heldTextValues.OrderBy(_ => random.Next())];
            if (random.Next(2) == 0)
                sourceValues[random.Next(sourceValues.Count)] = Texts[random.Next(Texts.Length)];
        }
        else
        {
            sourceValues = [.. Enumerable.Range(0, random.Next(1, heldCount > 10 ? 60 : 8)).Select(_ => Texts[random.Next(Texts.Length)])];
        }

        var heldTexts = mvo.AttributeValues.Where(av => av.AttributeId == TargetAttributeId).Select(av => av.StringValue).ToList();
        if (heldTexts.Where(text => text != null).GroupBy(text => text, StringComparer.Ordinal).Any(group => group.Count() > 1))
            features.Add(Feature.HeldDuplicate);
        if (sourceValues.GroupBy(text => text, StringComparer.Ordinal).Any(group => group.Count() > 1))
            features.Add(Feature.SourceDuplicate);
        if (heldTexts.Contains(null))
            features.Add(Feature.HeldWithoutText);
        if (heldTexts.Contains(string.Empty))
            features.Add(Feature.HeldEmptyText);
        if (sourceValues.Contains(string.Empty))
            features.Add(Feature.SourceEmptyText);
        if (mvo.AttributeValues.Any(av => av.AttributeId == OtherAttributeId && sourceValues.Contains(av.StringValue!, StringComparer.Ordinal)))
            features.Add(Feature.OtherAttributeHoldsSourceText);

        return (mvo, sourceValues, features);
    }

    private static MetaverseObjectAttributeValue HeldValue(MetaverseObject mvo, MetaverseAttribute attribute, string? text) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObject = mvo,
        Attribute = attribute,
        AttributeId = attribute.Id,
        StringValue = text,
        ContributedBySystemId = ConnectedSystemId,
        ContributedBySyncRuleId = SyncRuleId
    };

    private static string Describe(IEnumerable<MetaverseObjectAttributeValue> values) =>
        string.Join(", ", values.Select(av => $"{av.Id}:{Quote(av.StringValue)}"));

    private static string Quote(string? text) => text == null ? "null" : $"\"{text}\"";
}
