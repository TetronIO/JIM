// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;

namespace JIM.Application.Servers;

/// <summary>
/// The values a Connected System Object holds for one attribute, gathered once so that no-net-change detection can ask
/// of each value it stages whether the object already holds it (<see cref="HoldsMatchFor"/>) by lookup rather than by
/// scanning every held value (#2645). Asked per value of a large group, the scan cost the square of the membership: one
/// Delta Synchronisation spent 49.9 s on a 50,000-member group that gained two members and lost one.
/// </summary>
/// <remarks>
/// <para>
/// The answer is exactly that of comparing the change with every held value by <see cref="SyncEngine.ValuesMatch"/>, and
/// the lookups are keyed the way that comparison decides, so the two must change together; a differential test holds
/// them to one answer (<c>HeldAttributeValuesDifferentialTests</c>). <see cref="SyncEngine.ValuesMatch"/> decides on the
/// change's own fields, in a fixed order, which gives three kinds of question, each with its own lookup:
/// </para>
/// <list type="bullet">
/// <item>A change carrying an unresolved reference, or naming the object export resolution resolved it to, asks after a
/// reference by <see cref="SyncEngine.ReferenceValueMatches"/> (#2803): a change naming an object finds a resolved value by
/// that object alone and an unresolved one by its text; a change naming a Metaverse Object finds a value whose object is
/// joined to that Metaverse Object, and an unresolved one by its text; a change naming neither finds any value by its
/// text.</item>
/// <item>A change carrying text asks after that text: a held value with an unresolved reference compares it with that
/// reference (a distinguished name staged as text), and any other compares it with its own text, as the attribute compares
/// text.</item>
/// <item>Any other change asks after its first field holding a value, in the order number, long number, decimal, date,
/// boolean, GUID, binary, then an empty unresolved reference: a held value without text matches when its own first such
/// field is the same one and holds the same value (a date at the attribute's resolution), and a change holding nothing
/// matches a held value holding nothing.</item>
/// </list>
/// <para>
/// Text, references' text and dates compare by the change's attribute, so those lookups are built for the attribute of the
/// first change that asks; a later change whose attribute compares differently is answered by the scan instead, which is never wrong.
/// Each lookup is built on its first question, from one pass over the values, so a question never asked costs nothing and
/// nothing is read that the scan would not read. An instance belongs to one evaluation: it copies the values it is given,
/// and a later change to what the object holds is never seen by it.
/// </para>
/// </remarks>
internal sealed class HeldAttributeValues
{
    private readonly List<ConnectedSystemObjectAttributeValue> _values;

    private ReferenceLookup? _references;
    private TextLookup? _text;
    private TypedLookup? _typed;

    /// <param name="values">What the Connected System Object holds for the attribute; null or empty for nothing.</param>
    public HeldAttributeValues(IEnumerable<ConnectedSystemObjectAttributeValue>? values)
    {
        _values = values == null ? [] : [.. values];
    }

    /// <summary>
    /// The held values, in the order they were given.
    /// </summary>
    public IReadOnlyList<ConnectedSystemObjectAttributeValue> Values => _values;

    /// <summary>
    /// Whether any held value matches the change's value, exactly as comparing it with each by
    /// <see cref="SyncEngine.ValuesMatch"/> would say.
    /// </summary>
    public bool HoldsMatchFor(PendingExportAttributeValueChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (_values.Count == 0)
            return false;

        if (!string.IsNullOrEmpty(change.UnresolvedReferenceValue) || change.ResolvedReferenceCsoId.HasValue)
            return HoldsReference(change);

        if (change.StringValue != null)
            return HoldsText(change, change.StringValue);

        return HoldsTyped(change);
    }

    private bool HoldsReference(PendingExportAttributeValueChange change)
    {
        var references = ReferencesComparedAs(change);
        if (references == null)
            return Scan(change);

        // As SyncEngine.ReferenceValueMatches decides: by the object the change names, else by the Metaverse Object it
        // names, else by its text alone; each also by text, among the held values that way of naming compares by text.
        if (change.ResolvedReferenceCsoId is { } objectId)
            return references.ReferencedObjectIds.Contains(objectId) || HoldsReferenceText(references.TextOfUnresolved, change);

        if (SyncEngine.NamedMetaverseObjectId(change) is { } metaverseObjectId)
            return references.MetaverseObjectIds.Contains(metaverseObjectId) || HoldsReferenceText(references.TextOfUnplaced, change);

        return HoldsReferenceText(references.TextOfAll, change);
    }

    private static bool HoldsReferenceText(HashSet<string> heldText, PendingExportAttributeValueChange change) =>
        (!string.IsNullOrEmpty(change.UnresolvedReferenceValue) && heldText.Contains(change.UnresolvedReferenceValue)) ||
        (!string.IsNullOrEmpty(change.StringValue) && heldText.Contains(change.StringValue));

    private bool HoldsText(PendingExportAttributeValueChange change, string text)
    {
        var references = ReferencesComparedAs(change);
        var comparer = AttributeValueComparison.ComparerFor(change.Attribute);
        _text ??= TextLookup.Build(_values, comparer);
        if (references == null || !ReferenceEquals(_text.Comparer, comparer))
            return Scan(change);

        // Text names no object, so it finds a held reference by its text, resolved or not.
        return (text.Length > 0 && references.TextOfAll.Contains(text)) || _text.TextWithoutUnresolvedReference.Contains(text);
    }

    /// <summary>
    /// The held references, their text compared as the change's attribute compares it; null when they were gathered for an
    /// attribute that compares differently, which the scan then answers.
    /// </summary>
    private ReferenceLookup? ReferencesComparedAs(PendingExportAttributeValueChange change)
    {
        var comparer = AttributeValueComparison.ComparerFor(change.Attribute);
        _references ??= ReferenceLookup.Build(_values, comparer);
        return ReferenceEquals(_references.Comparer, comparer) ? _references : null;
    }

    private bool HoldsTyped(PendingExportAttributeValueChange change)
    {
        var comparer = AttributeValueComparison.ComparerFor(change.Attribute);
        var resolution = change.Attribute?.DateTimeResolution;
        _typed ??= TypedLookup.Build(_values, comparer, resolution);
        if (!ReferenceEquals(_typed.Comparer, comparer) || _typed.Resolution != resolution)
            return Scan(change);

        return _typed.HoldsMatchFor(change);
    }

    private bool Scan(PendingExportAttributeValueChange change) => _values.Exists(value => SyncEngine.ValuesMatch(change, value));

    /// <summary>
    /// The held references, each way <see cref="SyncEngine.ReferenceValueMatches"/> may find one: by the object a resolved
    /// value names, by the Metaverse Object that object is joined to, and by text among the values each way of naming
    /// compares by text.
    /// </summary>
    private sealed class ReferenceLookup
    {
        private ReferenceLookup(StringComparer comparer)
        {
            Comparer = comparer;
            TextOfAll = new HashSet<string>(comparer);
            TextOfUnresolved = new HashSet<string>(comparer);
            TextOfUnplaced = new HashSet<string>(comparer);
        }

        public StringComparer Comparer { get; }

        /// <summary>The objects resolved values name: what a change naming an object is found by.</summary>
        public HashSet<Guid> ReferencedObjectIds { get; } = [];

        /// <summary>The Metaverse Objects those objects are joined to: what a change naming a Metaverse Object is found by.</summary>
        public HashSet<Guid> MetaverseObjectIds { get; } = [];

        /// <summary>Every held reference's text: what a change naming no object is found by.</summary>
        public HashSet<string> TextOfAll { get; }

        /// <summary>The text of the values resolved to no object, which a change naming an object is also found by.</summary>
        public HashSet<string> TextOfUnresolved { get; }

        /// <summary>The text of the values neither resolved nor joined through to a Metaverse Object, which a change naming a
        /// Metaverse Object is also found by.</summary>
        public HashSet<string> TextOfUnplaced { get; }

        public static ReferenceLookup Build(List<ConnectedSystemObjectAttributeValue> values, StringComparer comparer)
        {
            var lookup = new ReferenceLookup(comparer);
            foreach (var value in values)
            {
                if (value.ReferenceValueId.HasValue)
                    lookup.ReferencedObjectIds.Add(value.ReferenceValueId.Value);

                var metaverseObjectId = value.ReferenceValue?.MetaverseObjectId;
                if (metaverseObjectId.HasValue)
                    lookup.MetaverseObjectIds.Add(metaverseObjectId.Value);

                if (value.UnresolvedReferenceValue == null)
                    continue;

                lookup.TextOfAll.Add(value.UnresolvedReferenceValue);
                if (value.ReferenceValueId.HasValue)
                    continue;

                lookup.TextOfUnresolved.Add(value.UnresolvedReferenceValue);
                if (!metaverseObjectId.HasValue)
                    lookup.TextOfUnplaced.Add(value.UnresolvedReferenceValue);
            }

            return lookup;
        }
    }

    /// <summary>
    /// The text of the held values with no unresolved reference, compared as the asking change's attribute compares text.
    /// </summary>
    private sealed class TextLookup
    {
        private TextLookup(StringComparer comparer)
        {
            Comparer = comparer;
            TextWithoutUnresolvedReference = new HashSet<string>(comparer);
        }

        public StringComparer Comparer { get; }

        public HashSet<string> TextWithoutUnresolvedReference { get; }

        public static TextLookup Build(List<ConnectedSystemObjectAttributeValue> values, StringComparer comparer)
        {
            var lookup = new TextLookup(comparer);
            lookup.TextWithoutUnresolvedReference.UnionWith(values
                .Where(value => value.UnresolvedReferenceValue == null && value.StringValue != null)
                .Select(value => value.StringValue!));
            return lookup;
        }
    }

    /// <summary>
    /// The held values with no text, each under its first field holding a value, for a change with neither text nor a
    /// reference.
    /// </summary>
    private sealed class TypedLookup
    {
        private HashSet<int>? _numbers;
        private HashSet<long>? _longNumbers;
        private HashSet<decimal>? _decimals;
        private List<DateTime>? _dates;
        private HashSet<long>? _dateKeys;
        private HashSet<bool>? _booleans;
        private HashSet<Guid>? _guids;
        // Binary values compared by content, as SyncEngine.ValuesMatch compares them.
        private HashSet<byte[]>? _binaries;
        private HashSet<string>? _unresolvedReferences;
        private bool _holdsNothing;

        private TypedLookup(StringComparer comparer, DateTimeResolution? resolution)
        {
            Comparer = comparer;
            Resolution = resolution;
        }

        public StringComparer Comparer { get; }

        public DateTimeResolution? Resolution { get; }

        public static TypedLookup Build(List<ConnectedSystemObjectAttributeValue> values, StringComparer comparer, DateTimeResolution? resolution)
        {
            var lookup = new TypedLookup(comparer, resolution);
            foreach (var value in values.Where(held => held.StringValue == null))
                lookup.Add(value);

            return lookup;
        }

        public bool HoldsMatchFor(PendingExportAttributeValueChange change)
        {
            if (change.IntValue.HasValue)
                return _numbers?.Contains(change.IntValue.Value) == true;
            if (change.LongValue.HasValue)
                return _longNumbers?.Contains(change.LongValue.Value) == true;
            if (change.DecimalValue.HasValue)
                return _decimals?.Contains(change.DecimalValue.Value) == true;
            if (change.DateTimeValue.HasValue)
                return HoldsDate(change.DateTimeValue.Value);
            if (change.BoolValue.HasValue)
                return _booleans?.Contains(change.BoolValue.Value) == true;
            if (change.GuidValue.HasValue)
                return _guids?.Contains(change.GuidValue.Value) == true;
            if (change.ByteValue != null)
                return _binaries?.Contains(change.ByteValue) == true;
            if (change.UnresolvedReferenceValue != null)
                return _unresolvedReferences?.Contains(change.UnresolvedReferenceValue) == true;

            return _holdsNothing;
        }

        private void Add(ConnectedSystemObjectAttributeValue value)
        {
            if (value.IntValue.HasValue)
                (_numbers ??= []).Add(value.IntValue.Value);
            else if (value.LongValue.HasValue)
                (_longNumbers ??= []).Add(value.LongValue.Value);
            // Decimal equality and hashing are numeric, so 5.0 and 5.00 are one member, as they are one value.
            else if (value.DecimalValue.HasValue)
                (_decimals ??= []).Add(value.DecimalValue.Value);
            else if (value.DateTimeValue.HasValue)
                (_dates ??= []).Add(value.DateTimeValue.Value);
            else if (value.BoolValue.HasValue)
                (_booleans ??= []).Add(value.BoolValue.Value);
            else if (value.GuidValue.HasValue)
                (_guids ??= []).Add(value.GuidValue.Value);
            else if (value.ByteValue != null)
                (_binaries ??= new HashSet<byte[]>(ByteArrayContentComparer.Instance)).Add(value.ByteValue);
            else if (value.UnresolvedReferenceValue != null)
                (_unresolvedReferences ??= new HashSet<string>(Comparer)).Add(value.UnresolvedReferenceValue);
            else
                _holdsNothing = true;
        }

        // Dates are keyed at the resolution only when a date is asked after, and the asking date only when one is held,
        // which is exactly when the scan computes a key.
        private bool HoldsDate(DateTime date)
        {
            if (_dates == null)
                return false;

            _dateKeys ??= _dates.Select(held => DateTimeAttributeValue.Key(held, Resolution)).ToHashSet();
            return _dateKeys.Contains(DateTimeAttributeValue.Key(date, Resolution));
        }
    }
}
