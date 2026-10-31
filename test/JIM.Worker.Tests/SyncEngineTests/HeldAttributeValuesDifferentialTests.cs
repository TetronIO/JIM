// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// No-net-change detection answers a per-value Add or Remove from a lookup of what the Connected System Object holds
/// (<see cref="HeldAttributeValues"/>, #2645) instead of comparing the value with every held value. The lookup must give
/// the scan's answer in every case, so these tests hold the two to one answer across a broad generated set of changes
/// and held values covering every way <see cref="SyncEngine.ValuesMatch"/> decides: references by Metaverse Object id and
/// by text, text as an identifier and as ordinary text, every data type, values holding several fields or none, dates of
/// every kind and resolution, and changes asked through attributes that compare differently.
/// </summary>
[TestFixture]
public class HeldAttributeValuesDifferentialTests
{
    private const int Seed = 2645;
    private const int HeldSetCount = 5_000;
    private const int ProbesPerHeldSet = 12;
    private const int MinimumOutcomesPerKind = 25;

    private static int _nextAttributeId = 5_000;

    private static readonly Guid[] MetaverseObjectIds =
    [
        Guid.Parse("6f1c1b8e-6f0a-4a57-9c1e-2b1d9f0a0001"),
        Guid.Parse("6f1c1b8e-6f0a-4a57-9c1e-2b1d9f0a0002"),
        Guid.Parse("6f1c1b8e-6f0a-4a57-9c1e-2b1d9f0a0003")
    ];

    // Text the generator draws for text and unresolved references alike: distinguished names differing only in case,
    // the Turkish capital dotted I that identifier comparison folds, text differing only in case, empty and blank
    // text, and Metaverse Object ids written the ways Guid.TryParse accepts.
    private static readonly string[] Texts =
    [
        "CN=Alice,OU=Users,DC=corp", "cn=alice,ou=users,dc=corp", "CN=Bob,OU=Users,DC=corp",
        "İnci", "inci", "INCI", "alpha", "Alpha", "", " ", "not-a-guid",
        MetaverseObjectIds[0].ToString(), MetaverseObjectIds[0].ToString().ToUpperInvariant(),
        MetaverseObjectIds[1].ToString("B"), MetaverseObjectIds[2].ToString("N")
    ];

    private static readonly int[] Numbers = [0, 1, 2];
    private static readonly long[] LongNumbers = [0L, 1L, 2L];
    private static readonly decimal[] Decimals = [5.0m, 5.00m, 6m, 0m];
    private static readonly Guid[] Guids = [MetaverseObjectIds[0], MetaverseObjectIds[1], Guid.Parse("0d6c0c55-1d2c-4f4e-8c5d-5a5a5a5a5a5a")];
    private static readonly byte[][] BinaryPatterns = [[1, 2, 3], [1, 2], [], [3, 2, 1]];

    private static readonly DateTime BaseDate = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    // The same instant in every Kind, a fraction below a microsecond away, within the same second, minute and day, the
    // next day, and the extremes that pass through canonicalisation unchanged.
    private static readonly DateTime[] Dates =
    [
        BaseDate.AddTicks(1_234_567), BaseDate.AddTicks(1_234_567).ToLocalTime(), DateTime.SpecifyKind(BaseDate.AddTicks(1_234_567), DateTimeKind.Unspecified),
        BaseDate.AddTicks(1_234_569), BaseDate.AddMilliseconds(400), BaseDate.AddSeconds(30), BaseDate.AddHours(2), BaseDate.AddDays(1),
        DateTime.MinValue, DateTime.MaxValue
    ];

    // Attributes the changes are asked through: ordinary text (compared exactly), identifiers ignoring case and
    // case-exact, references, dates at several resolutions, and none at all.
    private static readonly ConnectedSystemObjectTypeAttribute?[] Attributes =
    [
        Attribute(AttributeDataType.Text, AttributePlurality.MultiValued),
        Attribute(AttributeDataType.Text, AttributePlurality.SingleValued),
        Attribute(AttributeDataType.Text, AttributePlurality.SingleValued, attribute => attribute.IsExternalId = true),
        Attribute(AttributeDataType.Text, AttributePlurality.SingleValued, attribute => { attribute.IsSecondaryExternalId = true; attribute.CaseExact = true; }),
        Attribute(AttributeDataType.Reference, AttributePlurality.MultiValued),
        Attribute(AttributeDataType.Reference, AttributePlurality.MultiValued, attribute => attribute.CaseExact = true),
        Attribute(AttributeDataType.DateTime, AttributePlurality.MultiValued),
        Attribute(AttributeDataType.DateTime, AttributePlurality.MultiValued, attribute => attribute.DateTimeResolution = DateTimeResolution.Millisecond),
        Attribute(AttributeDataType.DateTime, AttributePlurality.SingleValued, attribute => attribute.DateTimeResolution = DateTimeResolution.Second),
        Attribute(AttributeDataType.DateTime, AttributePlurality.MultiValued, attribute => attribute.DateTimeResolution = DateTimeResolution.Day),
        Attribute(AttributeDataType.Number, AttributePlurality.MultiValued),
        null
    ];

    private static readonly PendingExportAttributeChangeType[] ChangeTypes =
    [
        PendingExportAttributeChangeType.Add,
        PendingExportAttributeChangeType.Remove,
        PendingExportAttributeChangeType.RemoveAll,
        PendingExportAttributeChangeType.Update
    ];

    /// <summary>
    /// How <see cref="SyncEngine.ValuesMatch"/> decides for a change: by the first of its fields it finds holding a value.
    /// </summary>
    private enum ProbeKind
    {
        ReferenceById,
        ReferenceByText,
        Text,
        Number,
        LongNumber,
        Decimal,
        DateTime,
        Boolean,
        Guid,
        Binary,
        EmptyUnresolvedReference,
        Nothing
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_LookupAndScan_GiveTheSameAnswerForEveryGeneratedChange()
    {
        var random = new Random(Seed);
        var disagreements = new List<string>();
        var outcomes = Enum.GetValues<ProbeKind>().ToDictionary(kind => kind, _ => (Matched: 0, Unmatched: 0));
        var askedThroughADifferentComparison = 0;

        for (var set = 0; set < HeldSetCount; set++)
        {
            var held = Enumerable.Range(0, random.Next(0, 9)).Select(_ => HeldValue(random)).ToList();
            var lookup = new HeldAttributeValues(held);

            // One attribute asks most of a set's questions, as one mapping asks them all in export staging; some are asked
            // through another, so a lookup built for one way of comparing is also asked by an attribute comparing another.
            var setAttribute = Attributes[random.Next(Attributes.Length)];
            (StringComparer Comparer, DateTimeResolution? Resolution)? firstAskedBy = null;

            for (var probe = 0; probe < ProbesPerHeldSet; probe++)
            {
                var attribute = random.Next(4) == 0 ? Attributes[random.Next(Attributes.Length)] : setAttribute;
                var (kind, value) = ChangeValue(random);
                var asking = (AttributeValueComparison.ComparerFor(attribute), attribute?.DateTimeResolution);
                if (kind is not (ProbeKind.ReferenceById or ProbeKind.ReferenceByText))
                {
                    firstAskedBy ??= asking;
                    if (!ReferenceEquals(firstAskedBy.Value.Comparer, asking.Item1) || firstAskedBy.Value.Resolution != asking.Item2)
                        askedThroughADifferentComparison++;
                }

                var matched = held.Exists(heldValue => SyncEngine.ValuesMatch(Change(value, attribute, PendingExportAttributeChangeType.Add), heldValue));
                var counts = outcomes[kind];
                outcomes[kind] = matched ? (counts.Matched + 1, counts.Unmatched) : (counts.Matched, counts.Unmatched + 1);

                foreach (var changeType in ChangeTypes)
                {
                    var change = Change(value, attribute, changeType);
                    var scanned = SyncEngine.IsCsoAttributeAlreadyCurrent(change, held);
                    var answered = SyncEngine.IsCsoAttributeAlreadyCurrent(change, lookup);
                    if (answered != scanned)
                        disagreements.Add($"{changeType} {kind} {Describe(change)} through {Describe(attribute)} against [{string.Join(" | ", held.Select(Describe))}]: scan {scanned}, lookup {answered}");
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreements, Is.Empty,
                $"{disagreements.Count:N0} disagreement(s); the first: {string.Join(Environment.NewLine, disagreements.Take(10))}");

            foreach (var (kind, (matchedCount, unmatchedCount)) in outcomes)
            {
                Assert.That(matchedCount, Is.GreaterThanOrEqualTo(MinimumOutcomesPerKind), $"too few generated {kind} changes matched a held value to cover the branch");
                Assert.That(unmatchedCount, Is.GreaterThanOrEqualTo(MinimumOutcomesPerKind), $"too few generated {kind} changes matched no held value to cover the branch");
            }

            Assert.That(askedThroughADifferentComparison, Is.GreaterThanOrEqualTo(MinimumOutcomesPerKind),
                "too few changes were asked through an attribute comparing differently from the one the lookup was built for");
        }
    }

    [Test]
    public void HoldsMatchFor_TextAskedThroughAnAttributeComparingDifferentlyFromTheFirst_AnswersAsThatAttributeCompares()
    {
        var ordinary = Attribute(AttributeDataType.Text, AttributePlurality.MultiValued);
        var identifier = Attribute(AttributeDataType.Text, AttributePlurality.SingleValued, attribute => attribute.IsExternalId = true);
        var lookup = new HeldAttributeValues([new ConnectedSystemObjectAttributeValue { StringValue = "CN=Alice,DC=corp" }]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup.HoldsMatchFor(Change(new GeneratedValue { StringValue = "cn=alice,dc=corp" }, ordinary, PendingExportAttributeChangeType.Add)), Is.False,
                "ordinary text compares exactly");
            Assert.That(lookup.HoldsMatchFor(Change(new GeneratedValue { StringValue = "cn=alice,dc=corp" }, identifier, PendingExportAttributeChangeType.Add)), Is.True,
                "an identifier ignores case, whichever attribute asked first");
        }
    }

    /// <summary>
    /// The lookup lives for one evaluation: a second evaluation of the same object sees what the object holds by then,
    /// never what the first evaluation saw.
    /// </summary>
    [Test]
    public void ComputeAttributeValueChanges_TargetValuesChangedBetweenEvaluations_SecondEvaluationAnswersFromTheNewValues()
    {
        var source = new MetaverseAttribute { Id = 1, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var target = Attribute(AttributeDataType.Reference, AttributePlurality.MultiValued);
        var rule = new SyncRule { Id = 7, Name = "Export", Direction = SyncRuleDirection.Export, Enabled = true };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 70,
            SyncRule = rule,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Id = 700, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });

        var memberMvoId = Guid.NewGuid();
        var group = new MetaverseObject { Id = Guid.NewGuid() };
        var member = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = group, Attribute = source, AttributeId = source.Id, ReferenceValueId = memberMvoId };
        group.AttributeValues.Add(member);
        var groupCso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var engine = new SyncEngine();

        var heldBefore = Array.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(value => (groupCso.Id, value.AttributeId));
        var before = engine.ComputeAttributeValueChanges(group, rule, [member], PendingExportChangeType.Update,
            existingCso: groupCso, csoAttributeCache: heldBefore, out _);

        var heldAfter = new[]
        {
            new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = groupCso, AttributeId = target.Id, Attribute = target,
                ReferenceValue = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = memberMvoId }
            }
        }.ToLookup(value => (groupCso.Id, value.AttributeId));
        var after = engine.ComputeAttributeValueChanges(group, rule, [member], PendingExportChangeType.Update,
            existingCso: groupCso, csoAttributeCache: heldAfter, out var alreadyCurrentCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Has.Count.EqualTo(1), "the member is not yet held, so it is added");
            Assert.That(after, Is.Empty, "the member is held by the second evaluation");
            Assert.That(alreadyCurrentCount, Is.EqualTo(1));
        }
    }

    #region Generation

    /// <summary>
    /// The value fields of a generated change, kept apart from its change type and attribute so the same value can be
    /// asked as every change type through any attribute.
    /// </summary>
    private sealed class GeneratedValue
    {
        public string? StringValue { get; init; }
        public int? IntValue { get; init; }
        public long? LongValue { get; init; }
        public decimal? DecimalValue { get; init; }
        public DateTime? DateTimeValue { get; init; }
        public bool? BoolValue { get; init; }
        public Guid? GuidValue { get; init; }
        public byte[]? ByteValue { get; init; }
        public string? UnresolvedReferenceValue { get; init; }
    }

    private static PendingExportAttributeValueChange Change(GeneratedValue value, ConnectedSystemObjectTypeAttribute? attribute, PendingExportAttributeChangeType changeType) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute!,
        AttributeId = attribute?.Id ?? 0,
        ChangeType = changeType,
        StringValue = value.StringValue,
        IntValue = value.IntValue,
        LongValue = value.LongValue,
        DecimalValue = value.DecimalValue,
        DateTimeValue = value.DateTimeValue,
        BoolValue = value.BoolValue,
        GuidValue = value.GuidValue,
        ByteValue = value.ByteValue,
        UnresolvedReferenceValue = value.UnresolvedReferenceValue
    };

    /// <summary>
    /// A change's value: one primary field as the probe kind says, and now and then other fields as well, so the order in
    /// which the comparison reads the fields is exercised too.
    /// </summary>
    private static (ProbeKind Kind, GeneratedValue Value) ChangeValue(Random random)
    {
        var primary = (ProbeKind)random.Next(Enum.GetValues<ProbeKind>().Length);
        if (primary == ProbeKind.Nothing)
            return (ProbeKind.Nothing, new GeneratedValue());

        var extras = random.Next(4) == 0;

        string? Maybe(Func<string> text) => extras && random.Next(3) == 0 ? text() : null;
        T? MaybeValue<T>(Func<T> pick) where T : struct => extras && random.Next(3) == 0 ? pick() : null;

        var value = new GeneratedValue
        {
            UnresolvedReferenceValue = primary switch
            {
                ProbeKind.ReferenceById => MetaverseObjectIdText(random),
                ProbeKind.ReferenceByText => PickNonGuidText(random),
                ProbeKind.EmptyUnresolvedReference => string.Empty,
                _ => extras && random.Next(4) == 0 ? string.Empty : null
            },
            StringValue = primary == ProbeKind.Text ? Pick(random, Texts) : Maybe(() => Pick(random, Texts)),
            IntValue = primary == ProbeKind.Number ? Pick(random, Numbers) : MaybeValue(() => Pick(random, Numbers)),
            LongValue = primary == ProbeKind.LongNumber ? Pick(random, LongNumbers) : MaybeValue(() => Pick(random, LongNumbers)),
            DecimalValue = primary == ProbeKind.Decimal ? Pick(random, Decimals) : MaybeValue(() => Pick(random, Decimals)),
            DateTimeValue = primary == ProbeKind.DateTime ? Pick(random, Dates) : MaybeValue(() => Pick(random, Dates)),
            BoolValue = primary == ProbeKind.Boolean ? random.Next(2) == 0 : MaybeValue(() => random.Next(2) == 0),
            GuidValue = primary == ProbeKind.Guid ? Pick(random, Guids) : MaybeValue(() => Pick(random, Guids)),
            ByteValue = primary == ProbeKind.Binary || (extras && random.Next(3) == 0) ? [.. Pick(random, BinaryPatterns)] : null
        };

        return (KindOf(value), value);
    }

    /// <summary>
    /// A held value, shaped as imports and provisioning leave them: one field holding a value, now and then several, and
    /// a reference that is resolved to an object joined to a Metaverse Object, resolved to one that is not, or unresolved.
    /// </summary>
    private static ConnectedSystemObjectAttributeValue HeldValue(Random random)
    {
        var primary = random.Next(11);
        var extras = random.Next(4) == 0;
        bool Extra() => extras && random.Next(3) == 0;

        var referenceValue = random.Next(3) switch
        {
            0 => null,
            1 => new ConnectedSystemObject { Id = Guid.NewGuid() },
            _ => new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = Pick(random, MetaverseObjectIds) }
        };

        return new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ReferenceValue = referenceValue,
            ReferenceValueId = referenceValue?.Id,
            UnresolvedReferenceValue = primary == 0 || Extra() ? Pick(random, Texts) : null,
            StringValue = primary == 1 || Extra() ? Pick(random, Texts) : null,
            IntValue = primary == 2 || Extra() ? Pick(random, Numbers) : null,
            LongValue = primary == 3 || Extra() ? Pick(random, LongNumbers) : null,
            DecimalValue = primary == 4 || Extra() ? Pick(random, Decimals) : null,
            DateTimeValue = primary == 5 || Extra() ? Pick(random, Dates) : null,
            BoolValue = primary == 6 || Extra() ? random.Next(2) == 0 : null,
            GuidValue = primary == 7 || Extra() ? Pick(random, Guids) : null,
            ByteValue = primary == 8 || Extra() ? [.. Pick(random, BinaryPatterns)] : null
            // 9 and 10: a value holding nothing but, perhaps, a resolved reference.
        };
    }

    private static ProbeKind KindOf(GeneratedValue value)
    {
        if (!string.IsNullOrEmpty(value.UnresolvedReferenceValue))
            return Guid.TryParse(value.UnresolvedReferenceValue, out _) ? ProbeKind.ReferenceById : ProbeKind.ReferenceByText;
        if (value.StringValue != null)
            return ProbeKind.Text;
        if (value.IntValue.HasValue)
            return ProbeKind.Number;
        if (value.LongValue.HasValue)
            return ProbeKind.LongNumber;
        if (value.DecimalValue.HasValue)
            return ProbeKind.Decimal;
        if (value.DateTimeValue.HasValue)
            return ProbeKind.DateTime;
        if (value.BoolValue.HasValue)
            return ProbeKind.Boolean;
        if (value.GuidValue.HasValue)
            return ProbeKind.Guid;
        if (value.ByteValue != null)
            return ProbeKind.Binary;

        return value.UnresolvedReferenceValue != null ? ProbeKind.EmptyUnresolvedReference : ProbeKind.Nothing;
    }

    private static string MetaverseObjectIdText(Random random)
    {
        var id = Pick(random, MetaverseObjectIds.Append(Guid.Parse("9b2f8a64-0c1d-4e3f-a5b6-c7d8e9f0a1b2")).ToArray());
        return random.Next(4) switch
        {
            0 => id.ToString(),
            1 => id.ToString().ToUpperInvariant(),
            2 => id.ToString("B"),
            _ => id.ToString("N")
        };
    }

    private static string PickNonGuidText(Random random)
    {
        var candidates = Texts.Where(text => text.Length > 0 && !Guid.TryParse(text, out _)).ToArray();
        return Pick(random, candidates);
    }

    private static T Pick<T>(Random random, T[] values) => values[random.Next(values.Length)];

    private static ConnectedSystemObjectTypeAttribute Attribute(AttributeDataType type, AttributePlurality plurality, Action<ConnectedSystemObjectTypeAttribute>? configure = null)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = ++_nextAttributeId, Name = $"attribute{_nextAttributeId}", Type = type, AttributePlurality = plurality };
        configure?.Invoke(attribute);
        return attribute;
    }

    private static string Describe(ConnectedSystemObjectTypeAttribute? attribute) => attribute == null
        ? "no attribute"
        : $"{attribute.Type}/{attribute.AttributePlurality}{(attribute.IsExternalId ? "/ExternalId" : "")}{(attribute.IsSecondaryExternalId ? "/SecondaryExternalId" : "")}" +
          $"{(attribute.CaseExact ? "/CaseExact" : "")}{(attribute.DateTimeResolution is { } resolution ? $"/{resolution}" : "")}";

    private static string Describe(PendingExportAttributeValueChange change) =>
        Describe(change.StringValue, change.IntValue, change.LongValue, change.DecimalValue, change.DateTimeValue, change.BoolValue,
            change.GuidValue, change.ByteValue, change.UnresolvedReferenceValue, referencedMetaverseObjectId: null, hasReference: false);

    private static string Describe(ConnectedSystemObjectAttributeValue value) =>
        Describe(value.StringValue, value.IntValue, value.LongValue, value.DecimalValue, value.DateTimeValue, value.BoolValue,
            value.GuidValue, value.ByteValue, value.UnresolvedReferenceValue, value.ReferenceValue?.MetaverseObjectId, value.ReferenceValue != null);

    private static string Describe(string? text, int? number, long? longNumber, decimal? decimalNumber, DateTime? date, bool? flag,
        Guid? guid, byte[]? bytes, string? unresolved, Guid? referencedMetaverseObjectId, bool hasReference)
    {
        var parts = new List<string>();
        if (text != null) parts.Add($"S'{text}'");
        if (number.HasValue) parts.Add($"I{number}");
        if (longNumber.HasValue) parts.Add($"L{longNumber}");
        if (decimalNumber.HasValue) parts.Add($"D{decimalNumber.Value.ToString(CultureInfo.InvariantCulture)}");
        if (date.HasValue) parts.Add($"T{date.Value.Ticks}/{date.Value.Kind}");
        if (flag.HasValue) parts.Add($"B{flag}");
        if (guid.HasValue) parts.Add($"G{guid}");
        if (bytes != null) parts.Add($"X[{string.Join(",", bytes)}]");
        if (unresolved != null) parts.Add($"U'{unresolved}'");
        if (hasReference) parts.Add($"R{referencedMetaverseObjectId?.ToString() ?? "-"}");
        return parts.Count == 0 ? "(nothing)" : string.Join(" ", parts);
    }

    #endregion
}
