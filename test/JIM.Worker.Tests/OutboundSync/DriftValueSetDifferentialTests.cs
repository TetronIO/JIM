// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
using JIM.Application.Services;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Drift Detection answers "does the other side hold a value equal to this one?" from a lookup of that side
/// (<see cref="DriftValueSet"/>, #2658) instead of comparing the value with every value there. The lookup must give the
/// scan's answer in every case, so these tests hold the two to one answer, in both directions the comparison is asked,
/// across a broad generated set of value sets and values covering every way
/// <see cref="DriftDetectionService.SingleValueEquals"/> decides: binary by content, text by each way an attribute compares
/// it, Guids by value and by text in every format that parses, numbers of different types holding the same number,
/// decimals differing only in scale, dates differing only in kind, booleans, null, and values of a type the lookups are
/// not keyed for (one whose hash disagrees with its equality, so only the scan answers it correctly).
/// </summary>
[TestFixture]
public class DriftValueSetDifferentialTests
{
    private const int Seed = 2658;
    private const int SetCount = 6_000;
    private const int ProbesPerSet = 14;
    private const int MinimumOutcomesPerKind = 25;

    private static readonly Guid[] Guids =
    [
        Guid.Parse("2658aaaa-0000-4000-8000-000000000001"),
        Guid.Parse("2658aaaa-0000-4000-8000-000000000002"),
        Guid.Parse("2658aaaa-0000-4000-8000-000000000003")
    ];

    // Distinguished names and text differing only in case, the Turkish capital dotted I that identifier comparison folds,
    // empty and blank text, and the first two Guids written every way Guid.TryParse accepts (the third is never text).
    private static readonly string[] Texts =
    [
        "CN=Alice,OU=Users,DC=corp", "cn=alice,ou=users,dc=corp", "CN=Bob,OU=Users,DC=corp",
        "İnci", "inci", "INCI", "alpha", "Alpha", "", " ", "not-a-guid", "1",
        Guids[0].ToString(), Guids[0].ToString().ToUpperInvariant(), Guids[0].ToString("B"), " " + Guids[0] + " ",
        Guids[1].ToString("N"), Guids[1].ToString("P")
    ];

    private static readonly byte[][] BinaryPatterns = [[1, 2, 3], [1, 2], [], [3, 2, 1]];

    private static readonly DateTime BaseDate = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    // The same instant in every kind, and other instants.
    private static readonly DateTime[] Dates =
    [
        BaseDate, DateTime.SpecifyKind(BaseDate, DateTimeKind.Local), DateTime.SpecifyKind(BaseDate, DateTimeKind.Unspecified),
        BaseDate.AddTicks(1), BaseDate.AddDays(1)
    ];

    // How the attribute compares text: exactly, and as an identifier that ignores case or is case-exact.
    private static readonly StringComparer[] TextComparers = [StringComparer.Ordinal, IdentifierKey.Comparer(caseExact: false), IdentifierKey.Comparer(caseExact: true)];

    /// <summary>
    /// What a value is, for the coverage check: each must be found and not found, asked about from each side.
    /// </summary>
    private enum ValueKind
    {
        Nothing,
        Binary,
        Text,
        Guid,
        Number,
        LongNumber,
        Decimal,
        Date,
        Boolean,
        Unkeyed
    }

    [Test]
    public void HoldsMatch_LookupAndScan_GiveTheSameAnswerForEveryGeneratedValueFromBothSides()
    {
        var random = new Random(Seed);
        var disagreements = new List<string>();
        var outcomes = new Dictionary<(ValueKind Kind, bool AskedAboutExpected), (int Matched, int Unmatched)>();
        var setsHoldingAnUnkeyedValue = 0;

        for (var set = 0; set < SetCount; set++)
        {
            var values = new HashSet<object>();
            var size = random.Next(0, 10);
            for (var i = 0; i < size; i++)
                values.Add(Value(random, allowUnkeyed: random.Next(8) == 0).Value!);
            if (random.Next(10) == 0)
                values.Add(null!);
            if (values.Any(value => value is Liberal))
                setsHoldingAnUnkeyedValue++;

            var textComparer = TextComparers[random.Next(TextComparers.Length)];
            var lookup = new DriftValueSet(values, textComparer);

            // Questions from both sides, in a random order, against one lookup, as Drift Detection asks them.
            for (var probe = 0; probe < ProbesPerSet; probe++)
            {
                var (kind, value) = Value(random, allowUnkeyed: random.Next(6) == 0);
                var askedAboutExpected = random.Next(2) == 0;

                var scan = askedAboutExpected
                    ? values.Any(actual => DriftDetectionService.SingleValueEquals(value, actual, textComparer))
                    : values.Any(expected => DriftDetectionService.SingleValueEquals(expected, value, textComparer));
                var answer = askedAboutExpected ? lookup.HoldsMatchForExpected(value) : lookup.HoldsMatchForActual(value);

                var key = (kind, askedAboutExpected);
                var counts = outcomes.GetValueOrDefault(key);
                outcomes[key] = scan ? (counts.Matched + 1, counts.Unmatched) : (counts.Matched, counts.Unmatched + 1);

                if (answer != scan && disagreements.Count < 20)
                {
                    disagreements.Add($"asked about the {(askedAboutExpected ? "expected" : "actual")} {kind} {Describe(value)} " +
                        $"against [{string.Join(", ", values.Select(Describe))}] comparing text {Describe(textComparer)}: lookup {answer}, scan {scan}");
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreements, Is.Empty, "the lookup must give the scan's answer in every case");
            Assert.That(setsHoldingAnUnkeyedValue, Is.GreaterThan(MinimumOutcomesPerKind), "sets holding a value the lookups are not keyed for are covered");

            foreach (var kind in Enum.GetValues<ValueKind>())
            {
                foreach (var askedAboutExpected in new[] { true, false })
                {
                    var (matched, unmatched) = outcomes.GetValueOrDefault((kind, askedAboutExpected));
                    var side = askedAboutExpected ? "expected" : "actual";
                    Assert.That(matched, Is.GreaterThanOrEqualTo(MinimumOutcomesPerKind), $"an {side} {kind} value that is held is covered");
                    Assert.That(unmatched, Is.GreaterThanOrEqualTo(MinimumOutcomesPerKind), $"an {side} {kind} value that is not held is covered");
                }
            }
        }
    }

    /// <summary>
    /// The comparison's one asymmetry, pinned on its own: an expected Guid matches a target holding its text, but expected
    /// text never matches a target holding the Guid it parses as.
    /// </summary>
    [Test]
    public void HoldsMatch_GuidAndItsText_MatchOnlyWhenTheGuidIsExpected()
    {
        var guid = Guids[0];
        var heldAsText = new DriftValueSet([guid.ToString()], StringComparer.Ordinal);
        var heldAsGuid = new DriftValueSet([guid], StringComparer.Ordinal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldAsText.HoldsMatchForExpected(guid), Is.True, "an expected Guid is held as its text");
            Assert.That(heldAsGuid.HoldsMatchForExpected(guid.ToString()), Is.False, "expected text is not held as a Guid");
            Assert.That(heldAsGuid.HoldsMatchForActual(guid.ToString()), Is.True, "held text is expected as the Guid it parses as");
            Assert.That(heldAsText.HoldsMatchForActual(guid), Is.False, "a held Guid is not expected as its text");
        }
    }

    private static (ValueKind Kind, object? Value) Value(Random random, bool allowUnkeyed)
    {
        if (allowUnkeyed)
            return (ValueKind.Unkeyed, new Liberal(Texts[random.Next(Texts.Length)]));

        return random.Next(10) switch
        {
            0 => random.Next(4) == 0 ? (ValueKind.Nothing, null) : (ValueKind.Text, Texts[random.Next(Texts.Length)]),
            // A new array each time: binary is held and asked after by content, never by the same instance.
            1 => (ValueKind.Binary, BinaryPatterns[random.Next(BinaryPatterns.Length)].ToArray()),
            2 or 3 => (ValueKind.Text, Texts[random.Next(Texts.Length)]),
            4 => (ValueKind.Guid, Guids[random.Next(Guids.Length)]),
            5 => (ValueKind.Number, random.Next(3)),
            6 => (ValueKind.LongNumber, (long)random.Next(3)),
            7 => (ValueKind.Decimal, new[] { 1m, 1.0m, 1.00m, 2m }[random.Next(4)]),
            8 => (ValueKind.Date, Dates[random.Next(Dates.Length)]),
            _ => (ValueKind.Boolean, random.Next(2) == 0)
        };
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        byte[] bytes => $"bytes[{string.Join(",", bytes)}]",
        string text => $"\"{text}\"",
        DateTime date => $"{date:O} ({date.Kind})",
        _ => $"{value} ({value.GetType().Name})"
    };

    private static string Describe(StringComparer comparer) =>
        ReferenceEquals(comparer, StringComparer.Ordinal) ? "exactly" : "ignoring case";

    /// <summary>
    /// A value of a type Drift Detection never reads values as, standing for any the lookups are not keyed for. Its
    /// equality is deliberately unlike any keyed type's: it equals anything whose text is its own, and its hash is its
    /// identity, so a hash lookup cannot answer for it or for a set holding it, and only the scan answers correctly.
    /// </summary>
    private sealed class Liberal(string text)
    {
        public override bool Equals(object? obj) => obj is Liberal other ? other.ToString() == text : obj?.ToString() == text;

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        public override string ToString() => text;
    }
}
