// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;

namespace JIM.Application.Servers;

/// <summary>
/// Puts the values a Connector hands JIM into the form JIM holds them (#2153), once, where they enter: before the
/// content hash, duplicate detection, the diff and the database write all see them. Pure; no I/O.
/// </summary>
/// <remarks>
/// A date is held to the microsecond, in UTC (<see cref="DateTimeAttributeValue.ToCanonical"/>). A Connector must not
/// normalise a date itself: it hands JIM what the source holds, and JIM decides what it can keep. Without this, a
/// source value with a sub-microsecond part was "changed" on every honest diff (the database keeps the microsecond)
/// and raised a false Verification Mode disagreement.
/// </remarks>
public static class ImportValueNormaliser
{
    /// <summary>
    /// Canonicalises every date the import object carries, in place.
    /// </summary>
    /// <returns>How many dates changed (a sub-microsecond part dropped, or a Kind other than UTC), for the run's
    /// summary.</returns>
    public static int CanonicaliseDates(ConnectedSystemImportObject importObject)
    {
        var changed = 0;
        foreach (var attribute in importObject.Attributes.Where(a => a.DateTimeValue.HasValue))
        {
            var held = attribute.DateTimeValue!.Value;
            var canonical = DateTimeAttributeValue.ToCanonical(held);
            if (canonical.Ticks == held.Ticks && canonical.Kind == held.Kind)
                continue;

            attribute.DateTimeValue = canonical;
            changed++;
        }

        return changed;
    }
}
