// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;

namespace JIM.Worker.Models;

/// <summary>
/// The External ID values an import read, over every page, by Object Type: what deletion detection and the Unconfirmed
/// Create retry compare JIM's objects against once the last page is in.
/// </summary>
/// <remarks>
/// Only the values are kept, in one set per Object Type and value type (#2521, Phase 3). The import used to keep each
/// object's External ID attribute as the Connector returned it, with its name and its typed value lists, until the end of
/// the run: about 212 MB at 500,000 objects. A set entry costs a few tens of bytes beside the value itself.
/// <para>
/// Text values are kept exactly as read (an ordinal set, so two values differing only in case are both kept) and folded
/// only when asked for ignoring case (<see cref="ForObjectType"/>), so the comparison follows the External ID attribute's
/// case-exact declaration (#2051) exactly as comparing against the attributes did, whichever declaration the caller
/// names. Every value type the attribute carried is kept, as the attributes were: deletion detection reads the type its
/// External ID declares, and the retry asks about whichever value a held object carries.
/// </para>
/// </remarks>
public sealed class ImportedExternalIds
{
    private readonly Dictionary<int, ObjectTypeValues> _byObjectType = new();

    /// <summary>
    /// How many distinct values are held, over every Object Type: the measure of what the run keeps for deletion
    /// detection.
    /// </summary>
    public long ValueCount { get; private set; }

    /// <summary>
    /// Keeps the values of one imported object's External ID attribute under its Object Type. An empty text value is not
    /// kept: the import removes empty values from every attribute it processes, so none is ever compared.
    /// </summary>
    public void Add(int objectTypeId, ConnectedSystemImportObjectAttribute externalIdAttribute)
    {
        if (!_byObjectType.TryGetValue(objectTypeId, out var values))
        {
            values = new ObjectTypeValues();
            _byObjectType[objectTypeId] = values;
        }

        // Only the lists that hold values are read: reading a list allocates it (#2521).
        if (externalIdAttribute.HasStringValues)
            ValueCount += AddAll(values.Strings ??= new HashSet<string>(StringComparer.Ordinal),
                externalIdAttribute.StringValues.Where(value => !string.IsNullOrEmpty(value)));
        if (externalIdAttribute.HasIntValues)
            ValueCount += AddAll(values.Ints ??= [], externalIdAttribute.IntValues);
        if (externalIdAttribute.HasLongValues)
            ValueCount += AddAll(values.Longs ??= [], externalIdAttribute.LongValues);
        if (externalIdAttribute.HasDecimalValues)
            ValueCount += AddAll(values.Decimals ??= [], externalIdAttribute.DecimalValues);
        if (externalIdAttribute.HasGuidValues)
            ValueCount += AddAll(values.Guids ??= [], externalIdAttribute.GuidValues);
    }

    /// <summary>
    /// The values read for one Object Type, with text compared as the External ID attribute declares (#2051): exactly
    /// when case-exact, else ignoring case. Ignoring case builds a folding set over the type's text values for the caller,
    /// which it holds only while it compares.
    /// </summary>
    public ImportedExternalIdValues ForObjectType(int objectTypeId, bool caseExact)
    {
        if (!_byObjectType.TryGetValue(objectTypeId, out var values))
            return ImportedExternalIdValues.Empty;

        IReadOnlySet<string>? strings = values.Strings == null || caseExact
            ? values.Strings
            : new HashSet<string>(values.Strings, IdentifierKey.Comparer(caseExact: false));

        return new ImportedExternalIdValues(strings, values.Ints, values.Longs, values.Decimals, values.Guids);
    }

    /// <summary>
    /// Lets every value go, once nothing more is to be compared against them.
    /// </summary>
    public void Clear()
    {
        _byObjectType.Clear();
        ValueCount = 0;
    }

    private static int AddAll<T>(HashSet<T> set, IEnumerable<T> values)
    {
        var before = set.Count;
        set.UnionWith(values);
        return set.Count - before;
    }

    /// <summary>One Object Type's values, each set made when the first value of its type arrives.</summary>
    private sealed class ObjectTypeValues
    {
        public HashSet<string>? Strings;
        public HashSet<int>? Ints;
        public HashSet<long>? Longs;
        public HashSet<decimal>? Decimals;
        public HashSet<Guid>? Guids;
    }
}
