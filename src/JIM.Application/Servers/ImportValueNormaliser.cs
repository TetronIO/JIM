// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;

namespace JIM.Application.Servers;

/// <summary>
/// Puts the values a Connector hands JIM into the form JIM holds them (#2153), once, where they enter: before the
/// content hash, duplicate detection, the diff and the database write all see them. Pure; no I/O.
/// </summary>
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
    /// Normalises one import object in place.
    /// </summary>
    public static void Normalise(ConnectedSystemImportObject importObject, IReadOnlyDictionary<string, AttributeDataType>? dateAndBooleanAttributes, ImportValueNormalisationSummary summary)
    {
        foreach (var attribute in importObject.Attributes.Where(a => a.HasDateTimeValues))
        {
            var values = attribute.DateTimeValues;
            for (var i = 0; i < values.Count; i++)
            {
                var held = values[i];
                var canonical = DateTimeAttributeValue.ToCanonical(held);
                if (canonical.Ticks == held.Ticks && canonical.Kind == held.Kind)
                    continue;

                values[i] = canonical;
                summary.DatesCanonicalised++;
            }
        }
    }
}
