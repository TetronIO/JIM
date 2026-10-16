// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Puts the values a Connector hands JIM into the form JIM holds them (#2153), once, where they enter: before the
/// content hash, duplicate detection, the diff and the database write all see them. Pure; no I/O.
/// </summary>
/// <remarks>
/// A Connector hands JIM what the source holds and does not normalise; JIM decides what it can keep. Without this, a
/// source date with a sub-microsecond part was "changed" on every honest diff (the database keeps the microsecond) and
/// raised a false Verification Mode disagreement, and each Connector chose among a Boolean's values its own way.
/// </remarks>
public static class ImportValueNormaliser
{
    /// <summary>The reason given for each of two or more dates JIM cannot keep apart.</summary>
    public const string SubMicrosecondCollisionReason =
        "Differs from another of this attribute's values by less than a microsecond, the finest time JIM holds, so JIM cannot keep both.";

    /// <summary>The reason given for each value of a true or false attribute that carried both true and false.</summary>
    public const string BooleanSurplusReason =
        "One of several values; a true or false attribute holds one, so JIM cannot tell which is meant.";

    /// <summary>
    /// The date and true or false attributes of an Object Type, by name (case-insensitive): the only attributes the
    /// normaliser acts on. Build it once per Object Type per run.
    /// </summary>
    public static IReadOnlyDictionary<string, AttributeDataType> BuildDateAndBooleanLookup(ConnectedSystemObjectType objectType)
    {
        var lookup = new Dictionary<string, AttributeDataType>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in objectType.Attributes.Where(a => a.Type is AttributeDataType.DateTime or AttributeDataType.Boolean))
            lookup.TryAdd(attribute.Name, attribute.Type);

        return lookup;
    }

    /// <summary>
    /// Normalises one import object in place: every date of a date attribute takes its canonical form
    /// (<see cref="DateTimeAttributeValue.ToCanonical"/>) and exact duplicates collapse; two or more dates that differ by
    /// less than a microsecond, and a true or false attribute carrying both true and false, are reported per value with
    /// <see cref="ImportAttributeValueErrorSource.Jim"/>, which leaves the attribute as JIM already held it.
    /// </summary>
    /// <param name="importObject">The object the Connector returned.</param>
    /// <param name="dateAndBooleanAttributes">The object's Object Type's date and true or false attributes
    /// (<see cref="BuildDateAndBooleanLookup"/>); null when the Object Type is unknown, which leaves the object alone
    /// for the import to reject.</param>
    /// <param name="summary">The run's counts, added to.</param>
    /// <remarks>
    /// An object already carrying an object-level error is left alone: reporting a value would mark it as carrying an
    /// attribute value error instead, turning a skip-the-object error into an import-the-rest one. An attribute the
    /// Connector already reported is left alone too; JIM leaves it as held either way. A single value allocates nothing.
    /// </remarks>
    public static void Normalise(ConnectedSystemImportObject importObject, IReadOnlyDictionary<string, AttributeDataType>? dateAndBooleanAttributes, ImportValueNormalisationSummary summary)
    {
        if (dateAndBooleanAttributes is not { Count: > 0 })
            return;

        if (importObject.ErrorType is not (null or ConnectedSystemImportObjectError.NotSet or ConnectedSystemImportObjectError.AttributeValueError))
            return;

        // Indexed rather than foreach: reporting a value adds to the object's error list, not to its attributes, but the
        // loop must not allocate an enumerator per object on the hot path either.
        var attributes = importObject.Attributes;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            var hasDates = attribute.HasDateTimeValues;
            var hasBools = attribute.HasBoolValues;
            if (!hasDates && !hasBools)
                continue;

            if (!dateAndBooleanAttributes.TryGetValue(attribute.Name, out var type))
                continue;

            if (IsReportedByConnector(importObject, attribute.Name))
                continue;

            if (type == AttributeDataType.DateTime && hasDates)
                NormaliseDates(importObject, attribute, summary);
            else if (type == AttributeDataType.Boolean && hasBools)
                NormaliseBooleans(importObject, attribute, summary);
        }
    }

    private static void NormaliseDates(ConnectedSystemImportObject importObject, ConnectedSystemImportObjectAttribute attribute, ImportValueNormalisationSummary summary)
    {
        var values = attribute.DateTimeValues;

        // The fast path: one value, canonicalised in place.
        if (values.Count == 1)
        {
            values[0] = Canonicalise(values[0], summary);
            return;
        }

        // Each value as an instant at the full 100ns (UTC ticks), so the same instant in two Kinds is one value, and two
        // instants less than a microsecond apart are two values with one canonical key.
        var instants = new List<long>(values.Count);
        var seenInstants = new HashSet<long>();
        foreach (var value in values)
        {
            var instant = ToUtcTicks(value);
            if (seenInstants.Add(instant))
                instants.Add(instant);
            else
                summary.DuplicatesCollapsed++;
        }

        var byKey = new Dictionary<long, int>(instants.Count);
        foreach (var instant in instants)
            byKey[Floor(instant)] = byKey.TryGetValue(Floor(instant), out var count) ? count + 1 : 1;

        if (byKey.Count < instants.Count)
        {
            foreach (var instant in instants.Where(instant => byKey[Floor(instant)] > 1))
            {
                importObject.AddAttributeValueError(attribute.Name, ToSevenDigitString(instant), SubMicrosecondCollisionReason, ImportAttributeValueErrorSource.Jim);
                summary.CollisionsReported++;
            }

            return;
        }

        if (instants.Count < values.Count)
            Log.Debug("ImportValueNormaliser: Collapsed {DuplicateCount} duplicate date value(s) of attribute '{AttributeName}'",
                values.Count - instants.Count, LogSanitiser.Sanitise(attribute.Name));

        summary.DatesCanonicalised += values.Count(value => IsChangedByCanonicalising(value, DateTimeAttributeValue.ToCanonical(value)));

        // The distinct values in the order first read, each in its canonical form.
        var canonicalValues = new List<DateTime>(instants.Count);
        foreach (var instant in instants)
            canonicalValues.Add(new DateTime(Floor(instant), DateTimeKind.Utc));

        attribute.DateTimeValues = canonicalValues;
    }

    private static void NormaliseBooleans(ConnectedSystemImportObject importObject, ConnectedSystemImportObjectAttribute attribute, ImportValueNormalisationSummary summary)
    {
        var values = attribute.BoolValues;
        if (values.Count == 1)
            return;

        var first = values[0];
        var mixed = values.Exists(v => v != first);
        if (mixed)
        {
            // Whatever plurality the schema declares: a true or false attribute holds one value, and JIM cannot tell
            // which of the two is meant, so it reports both and leaves the attribute as it held it.
            importObject.AddAttributeValueError(attribute.Name, "true", BooleanSurplusReason, ImportAttributeValueErrorSource.Jim);
            importObject.AddAttributeValueError(attribute.Name, "false", BooleanSurplusReason, ImportAttributeValueErrorSource.Jim);
            summary.BooleanSurplusesReported += 2;
            return;
        }

        summary.DuplicatesCollapsed += values.Count - 1;
        Log.Debug("ImportValueNormaliser: Collapsed {DuplicateCount} duplicate true or false value(s) of attribute '{AttributeName}'",
            values.Count - 1, LogSanitiser.Sanitise(attribute.Name));
        values.RemoveRange(1, values.Count - 1);
    }

    private static DateTime Canonicalise(DateTime held, ImportValueNormalisationSummary summary)
    {
        var canonical = DateTimeAttributeValue.ToCanonical(held);
        if (IsChangedByCanonicalising(held, canonical))
            summary.DatesCanonicalised++;

        return canonical;
    }

    private static bool IsChangedByCanonicalising(DateTime held, DateTime canonical) =>
        canonical.Ticks != held.Ticks || canonical.Kind != held.Kind;

    private static bool IsReportedByConnector(ConnectedSystemImportObject importObject, string attributeName) =>
        importObject.AttributeValueErrors is { Count: > 0 } errors &&
        errors.Exists(error => error.AttributeName.Equals(attributeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The instant a date names, in UTC ticks at its full precision; the canonical key's input.</summary>
    private static long ToUtcTicks(DateTime value)
    {
        if (value == DateTime.MinValue || value == DateTime.MaxValue)
            return value.Ticks;

        return value.Kind == DateTimeKind.Local ? value.ToUniversalTime().Ticks : value.Ticks;
    }

    private static long Floor(long utcTicks) => DateTimeAttributeValue.Key(new DateTime(utcTicks, DateTimeKind.Utc));

    /// <summary>A value as received, at the seven digits a DateTime holds, so two values less than a microsecond apart
    /// read as different in the report.</summary>
    private static string ToSevenDigitString(long utcTicks) =>
        new DateTime(utcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
