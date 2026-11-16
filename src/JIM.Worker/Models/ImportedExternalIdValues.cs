// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Models;

/// <summary>
/// The External ID values an import read for one Object Type, by value type (<see cref="ImportedExternalIds.ForObjectType"/>),
/// with text compared as the type's External ID attribute declares (#2051). Decimal values compare numerically, so 4200.00
/// and 4200 are the same value.
/// </summary>
public sealed class ImportedExternalIdValues
{
    /// <summary>Nothing was read for the Object Type.</summary>
    public static readonly ImportedExternalIdValues Empty = new(null, null, null, null, null);

    internal ImportedExternalIdValues(IReadOnlySet<string>? strings, IReadOnlySet<int>? ints, IReadOnlySet<long>? longs,
        IReadOnlySet<decimal>? decimals, IReadOnlySet<Guid>? guids)
    {
        Strings = strings ?? new HashSet<string>();
        Ints = ints ?? new HashSet<int>();
        Longs = longs ?? new HashSet<long>();
        Decimals = decimals ?? new HashSet<decimal>();
        Guids = guids ?? new HashSet<Guid>();
    }

    public IReadOnlySet<string> Strings { get; }

    public IReadOnlySet<int> Ints { get; }

    public IReadOnlySet<long> Longs { get; }

    public IReadOnlySet<decimal> Decimals { get; }

    public IReadOnlySet<Guid> Guids { get; }

    /// <summary>
    /// Whether any of a held object's External ID values was read: the typed columns of one held value, any of which may
    /// be null.
    /// </summary>
    public bool Contains(string? stringValue, int? intValue, long? longValue, decimal? decimalValue, Guid? guidValue) =>
        (stringValue != null && Strings.Contains(stringValue)) ||
        (intValue.HasValue && Ints.Contains(intValue.Value)) ||
        (longValue.HasValue && Longs.Contains(longValue.Value)) ||
        (decimalValue.HasValue && Decimals.Contains(decimalValue.Value)) ||
        (guidValue.HasValue && Guids.Contains(guidValue.Value));
}
