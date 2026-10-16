// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;
using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Applies a modify the directory refused because a value it carries was already in the state the export asked for (a
/// value to add already present, one to remove already absent), in parts, halving only the parts the directory refuses
/// again until each value it refuses stands alone.
/// </summary>
/// <remarks>
/// <para>
/// The caller drives it: <see cref="TryTakeNext"/> gives the next request to send, and the caller reports the
/// directory's answer through <see cref="Applied"/>, or, for a refusal <see cref="IsAlreadyInDesiredState"/> accepts,
/// <see cref="RefusedAsAlreadyInDesiredState"/>. Any other refusal is the caller's to raise, and fails the export.
/// Keeping the sending with the caller lets the synchronous and asynchronous export paths share this one plan.
/// </para>
/// <para>
/// <b>Order.</b> Atomically, a directory checks the entry against its schema once, after every change; in parts, it
/// checks after each. So object classes being added go first and those being removed go last, and every replacement
/// goes together in one request. A value deleted and added again under a spelling the directory may treat as the same
/// (the key <see cref="LdapValueCollisionKey"/> gives: <c>SMTP:jane.doe@x</c> and <c>smtp:jane.doe@x</c>, or a member's
/// Distinguished Name in a new case) goes next, its delete and its add in one request, delete first, because the
/// directory compares values with its own matching rule: an add sent alone first is accepted where nothing matches it,
/// or skipped under Permissive Modify where the old spelling still does, and the delete of the old spelling then takes
/// the value away. Every other value is added before any is deleted, so an attribute an entry must hold (groupOfNames'
/// member) never passes through empty on its way from the old values to the new. A single-valued attribute whose value
/// the export changes by a delete and an add (a multi-valued Metaverse attribute flowing to it, #435) goes after those
/// deletes, its deletes and its adds in one request, delete first: OpenLDAP and 389 Directory Server refuse an add to a
/// single-valued attribute still holding another value as a constraint violation, which says nothing about a value
/// already in place. Going after the other values means that where the attribute holds a value the export does not
/// replace, which fails the export, every other change has landed first.
/// </para>
/// <para>
/// <b>Halving.</b> An attribute's pairs go as one request, as do its other adds, and separately its other deletes, and
/// a single-valued change goes as one request too. A part the directory refuses as already in place is split in two
/// and each half sent; only a half refused again is split further. Pairs are split between keys, never within one, so
/// no add is ever sent ahead of the delete it pairs with; one pair refused on its own is sent as its delete, then its
/// add, and so is a single-valued change, whose old and new values never share a key. One value refused on its own is
/// in place. For k such values among n, that is about 2k log2(n) requests, where sending every value alone took n: on
/// OpenLDAP every accepted modify of a large group rewrites the entry and, with the accesslog's logold, logs every old
/// member, so one stale member among a thousand used to cost 999 heavy writes. A part that is exactly the refused modify
/// has already been answered as a whole, so it is split straight away rather than sent again.
/// </para>
/// <para>
/// <b>An add refused as already present is not always done.</b> While deletes of that attribute are still to come, the
/// value present may be one of those they remove, compared as the directory compares it, so such an add is held back,
/// sent again after those deletes, and is done only if the directory refuses it as already present once more. And
/// where the Connected System's schema says the attribute is single-valued (a multi-valued Metaverse attribute flowing
/// to it, #435), Active Directory and Samba AD refuse an add with the same answer when the attribute holds any other
/// value, so a refused add there says nothing about which value is held and is never treated as done: the export fails,
/// as it did before the fallback existed. An unrecognised directory is read the same way, as the cautious reading.
/// OpenLDAP and 389 Directory Server give that answer only when the attribute holds that very value (a different one
/// is a constraint violation), so there it means what it says. A delete refused as already absent is always done.
/// </para>
/// </remarks>
internal sealed class LdapModifyBisection
{
    private readonly string _dn;
    private readonly IReadOnlySet<string> _singleValuedAttributes;
    private readonly bool _singleValuedAddRefusalNamesNoValue;
    private readonly HashSet<string> _attributesWithDeletes;
    private readonly Func<DirectoryOperationException, DirectoryAttributeModification, bool> _meansAlreadyInDesiredState;
    private readonly Queue<Func<IEnumerable<Step>>> _phases;
    private readonly LinkedList<Step> _queue = new();

    // Adds refused as already present while deletes of the same attribute were still to come, one modification per
    // attribute, in the order they were first held back.
    private readonly List<DirectoryAttributeModification> _heldBack = [];

    // The step most recently handed out and not yet answered.
    private Step? _current;

    /// <summary>
    /// Starts the plan for a refused modify.
    /// </summary>
    /// <param name="refused">The modify the directory refused as a whole.</param>
    /// <param name="singleValuedAttributes">The attributes the Connected System's schema says hold one value at most,
    /// compared without regard to case.</param>
    /// <param name="directoryType">The kind of directory, which decides what a refused add to a single-valued attribute
    /// says about the value it holds.</param>
    /// <param name="meansAlreadyInDesiredState">Whether a refusal says the value of a modification is already where the
    /// export wants it; the directory's own codes differ.</param>
    internal LdapModifyBisection(
        ModifyRequest refused,
        IReadOnlySet<string> singleValuedAttributes,
        LdapDirectoryType directoryType,
        Func<DirectoryOperationException, DirectoryAttributeModification, bool> meansAlreadyInDesiredState)
    {
        _dn = refused.DistinguishedName;
        _singleValuedAttributes = singleValuedAttributes;
        _singleValuedAddRefusalNamesNoValue =
            directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD or LdapDirectoryType.Generic;
        _meansAlreadyInDesiredState = meansAlreadyInDesiredState;

        var modifications = refused.Modifications.Cast<DirectoryAttributeModification>().ToList();
        var valueChanges = modifications.Where(IsValueChange).ToList();
        ValueChanges = valueChanges.Sum(ValueCount);

        var (pairs, unpairedValueChanges) = SeparatePairs(valueChanges);
        var (singleValuedChanges, unpaired) = SeparateSingleValuedChanges(unpairedValueChanges, singleValuedAttributes);
        _attributesWithDeletes = unpaired
            .Where(modification => modification.Operation == DirectoryAttributeOperation.Delete)
            .Select(modification => modification.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The refused modify has already answered for a part that carries exactly what it carried: one attribute's adds
        // or deletes, or one attribute's pairs or single-valued change and nothing else.
        var answeredAsAWhole = modifications.Count == 1;
        var deleteThenAddAnsweredAsAWhole = pairs.Count + singleValuedChanges.Count == 1 && unpaired.Count == 0 &&
                                            valueChanges.Count == modifications.Count;
        _phases = new Queue<Func<IEnumerable<Step>>>(
        [
            () => Parts(unpaired, DirectoryAttributeOperation.Add, objectClasses: true, answeredAsAWhole),
            () => Replacements(modifications),
            () => pairs.Select(pair => new Step(pair, SentAgain: false, deleteThenAddAnsweredAsAWhole, IsPair: true)),
            () => Parts(unpaired, DirectoryAttributeOperation.Add, objectClasses: false, answeredAsAWhole),
            () => Parts(unpaired, DirectoryAttributeOperation.Delete, objectClasses: false, answeredAsAWhole),
            () => singleValuedChanges.Select(change =>
                new Step(change, SentAgain: false, deleteThenAddAnsweredAsAWhole, IsSingleValuedChange: true)),
            () => HeldBack(objectClasses: false),
            () => Parts(unpaired, DirectoryAttributeOperation.Delete, objectClasses: true, answeredAsAWhole),
            () => HeldBack(objectClasses: true)
        ]);
    }

    /// <summary>
    /// How many values the refused modify added or deleted; a delete of a whole attribute counts as one.
    /// </summary>
    internal int ValueChanges { get; }

    /// <summary>
    /// How many of those values were found already in the state the export asked for.
    /// </summary>
    internal int ValuesAlreadyInDesiredState { get; private set; }

    /// <summary>
    /// How many requests the plan has handed out.
    /// </summary>
    internal int RequestsSent { get; private set; }

    /// <summary>
    /// The next request to send, or false when every part has been applied or found already in place.
    /// </summary>
    internal bool TryTakeNext([NotNullWhen(true)] out ModifyRequest? request)
    {
        if (_current != null)
            throw new InvalidOperationException("The directory's answer to the previous request has not been reported.");

        while (TryDequeue(out var step))
        {
            if (step.AnsweredAsAWhole && step.DeletesThenAdds)
            {
                SplitDeletesThenAdds(step);
                continue;
            }

            if (step.AnsweredAsAWhole && step.ValueGroup is { Count: > 1 })
            {
                SplitIntoHalves(step);
                continue;
            }

            _current = step;
            RequestsSent++;
            request = new ModifyRequest(_dn);
            request.Modifications.AddRange(step.Modifications.Select(CopyOf).ToArray());
            return true;
        }

        request = null;
        return false;
    }

    /// <summary>
    /// The directory applied the request <see cref="TryTakeNext"/> gave.
    /// </summary>
    internal void Applied() => TakeCurrent();

    /// <summary>
    /// Whether this refusal of the request <see cref="TryTakeNext"/> gave can be resolved by the plan: the request
    /// carried one attribute's pairs, single-valued change, adds or deletes, the directory says a value among them is
    /// already in place, and, for a lone add, the rules for adds (see the remarks on this class) allow it. Changes
    /// nothing, so it is safe in an exception filter; a refusal it does not accept fails the export.
    /// </summary>
    internal bool IsAlreadyInDesiredState(DirectoryOperationException refusal)
    {
        if (_current is { DeletesThenAdds: true } deletesThenAdds)
            return deletesThenAdds.Modifications.Any(modification => _meansAlreadyInDesiredState(refusal, modification));

        if (_current is not { ValueGroup: { } part } step || !_meansAlreadyInDesiredState(refusal, part))
            return false;

        return part.Count > 1 || MustBeSentAgainAfterTheDeletes(step, part) || !MayHoldAnotherValue(part);
    }

    /// <summary>
    /// The directory refused the request <see cref="TryTakeNext"/> gave in a way <see cref="IsAlreadyInDesiredState"/>
    /// accepted: split it, hold back a lone add to send again after the deletes, or count a lone value as in place.
    /// </summary>
    internal void RefusedAsAlreadyInDesiredState()
    {
        var step = TakeCurrent();
        if (step.DeletesThenAdds)
        {
            SplitDeletesThenAdds(step);
            return;
        }

        var part = step.ValueGroup ?? throw new InvalidOperationException("Only a request carrying one attribute's values can be refused as already in place.");

        if (part.Count > 1)
            SplitIntoHalves(step);
        else if (MustBeSentAgainAfterTheDeletes(step, part))
            HoldBack(part);
        else
            ValuesAlreadyInDesiredState += ValueCount(part);
    }

    private Step TakeCurrent()
    {
        var step = _current ?? throw new InvalidOperationException("No request is awaiting the directory's answer.");
        _current = null;
        return step;
    }

    private bool TryDequeue([NotNullWhen(true)] out Step? step)
    {
        while (_queue.Count == 0 && _phases.Count > 0)
        {
            foreach (var next in _phases.Dequeue()())
                _queue.AddLast(next);
        }

        step = _queue.First?.Value;
        if (step != null)
            _queue.RemoveFirst();
        return step != null;
    }

    // An add refused as present before the deletes sent apart from their pairs have gone: the value present may be one
    // of those the deletes remove, compared as the directory compares it, so it is sent again once they have gone.
    private bool MustBeSentAgainAfterTheDeletes(Step step, DirectoryAttributeModification part) =>
        part.Operation == DirectoryAttributeOperation.Add && !step.SentAgain && _attributesWithDeletes.Contains(part.Name);

    // Where a refused add to a single-valued attribute names no value, the attribute may hold another one.
    private bool MayHoldAnotherValue(DirectoryAttributeModification part) =>
        _singleValuedAddRefusalNamesNoValue && part.Operation == DirectoryAttributeOperation.Add && _singleValuedAttributes.Contains(part.Name);

    private void SplitIntoHalves(Step step)
    {
        var part = step.ValueGroup!;
        var half = part.Count / 2;
        _queue.AddFirst(new Step([Slice(part, half, part.Count)], step.SentAgain, AnsweredAsAWhole: false));
        _queue.AddFirst(new Step([Slice(part, 0, half)], step.SentAgain, AnsweredAsAWhole: false));
    }

    /// <summary>
    /// Splits a step that deletes and then adds: pairs between keys (see <see cref="SplitPairs"/>), and a single-valued
    /// change into its delete, then its add.
    /// </summary>
    private void SplitDeletesThenAdds(Step step)
    {
        if (step.IsPair)
            SplitPairs(step);
        else
            SplitIntoTheDeleteThenTheAdd(step);
    }

    /// <summary>
    /// Splits one attribute's pairs between keys, each half still a delete followed by an add; a lone pair becomes its
    /// delete, then its add, so the add never reaches the directory ahead of the delete it pairs with.
    /// </summary>
    private void SplitPairs(Step step)
    {
        var (delete, add) = (step.Modifications[0], step.Modifications[1]);
        var deletesByKey = LdapValueCollisionKey.Group(Values(delete));
        var addsByKey = LdapValueCollisionKey.Group(Values(add));
        var keys = deletesByKey.Keys.ToList();
        if (keys.Count == 0)
            throw new InvalidOperationException("A request carrying pairs must delete at least one value.");

        if (keys.Count == 1)
        {
            SplitIntoTheDeleteThenTheAdd(step);
            return;
        }

        var half = keys.Count / 2;
        _queue.AddFirst(PairsFor(delete.Name, keys.Skip(half), deletesByKey, addsByKey));
        _queue.AddFirst(PairsFor(delete.Name, keys.Take(half), deletesByKey, addsByKey));
    }

    /// <summary>
    /// Sends a step's delete on its own, then its add on its own, so the add still follows the delete. A single-valued
    /// change cannot be split between keys as pairs are: its old value and its new one never share a key.
    /// </summary>
    private void SplitIntoTheDeleteThenTheAdd(Step step)
    {
        var (delete, add) = (step.Modifications[0], step.Modifications[1]);
        _queue.AddFirst(new Step([add], SentAgain: false, AnsweredAsAWhole: false));
        _queue.AddFirst(new Step([delete], SentAgain: false, AnsweredAsAWhole: false));
    }

    private static Step PairsFor(
        string attributeName,
        IEnumerable<string> keys,
        Dictionary<string, List<object?>> deletesByKey,
        Dictionary<string, List<object?>> addsByKey)
    {
        var keyList = keys.ToList();
        return new Step(
            [
                NewModification(attributeName, DirectoryAttributeOperation.Delete, keyList.SelectMany(key => deletesByKey[key])),
                NewModification(attributeName, DirectoryAttributeOperation.Add, keyList.SelectMany(key => addsByKey[key]))
            ],
            SentAgain: false,
            AnsweredAsAWhole: false,
            IsPair: true);
    }

    private void HoldBack(DirectoryAttributeModification part)
    {
        var heldBack = _heldBack.Find(modification => modification.Name.Equals(part.Name, StringComparison.OrdinalIgnoreCase));
        if (heldBack == null)
        {
            heldBack = new DirectoryAttributeModification { Name = part.Name, Operation = DirectoryAttributeOperation.Add };
            _heldBack.Add(heldBack);
        }

        for (var i = 0; i < part.Count; i++)
            AddValue(heldBack, part[i]);
    }

    private IEnumerable<Step> HeldBack(bool objectClasses) =>
        _heldBack
            .Where(modification => IsObjectClass(modification) == objectClasses)
            .Select(modification => new Step([modification], SentAgain: true, AnsweredAsAWhole: false));

    /// <summary>
    /// Takes out of the refused modify's value changes each value deleted and added again under the same key, one pair
    /// modification (a delete, then an add) per attribute that has any, and returns what is left in the order it stood.
    /// A delete of a whole attribute names no value, so it pairs with nothing.
    /// </summary>
    private static (List<DirectoryAttributeModification[]> Pairs, List<DirectoryAttributeModification> Unpaired) SeparatePairs(
        List<DirectoryAttributeModification> valueChanges)
    {
        var pairedKeysByAttribute = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var attributeChanges in valueChanges.GroupBy(modification => modification.Name, StringComparer.OrdinalIgnoreCase))
        {
            var deleteKeys = LdapValueCollisionKey.Group(attributeChanges
                .Where(modification => modification.Operation == DirectoryAttributeOperation.Delete)
                .SelectMany(Values)).Keys;
            var addKeys = LdapValueCollisionKey.Group(attributeChanges
                .Where(modification => modification.Operation == DirectoryAttributeOperation.Add)
                .SelectMany(Values)).Keys;

            var pairedKeys = deleteKeys.Where(addKeys.Contains).ToHashSet(LdapValueCollisionKey.Comparer);
            if (pairedKeys.Count > 0)
                pairedKeysByAttribute[attributeChanges.Key] = pairedKeys;
        }

        var pairs = new Dictionary<string, (List<object?> Deletes, List<object?> Adds, string Name)>(StringComparer.OrdinalIgnoreCase);
        var unpaired = new List<DirectoryAttributeModification>();
        foreach (var modification in valueChanges)
        {
            if (modification.Count == 0 || !pairedKeysByAttribute.TryGetValue(modification.Name, out var pairedKeys))
            {
                unpaired.Add(modification);
                continue;
            }

            if (!pairs.TryGetValue(modification.Name, out var pair))
            {
                pair = ([], [], modification.Name);
                pairs[modification.Name] = pair;
            }

            var values = Values(modification).ToLookup(value => LdapValueCollisionKey.Of(value) is { } key && pairedKeys.Contains(key));
            (modification.Operation == DirectoryAttributeOperation.Delete ? pair.Deletes : pair.Adds).AddRange(values[true]);

            if (values[false].Any())
                unpaired.Add(NewModification(modification.Name, modification.Operation, values[false]));
        }

        return ([.. pairs.Values.Select(pair => new[]
        {
            NewModification(pair.Name, DirectoryAttributeOperation.Delete, pair.Deletes),
            NewModification(pair.Name, DirectoryAttributeOperation.Add, pair.Adds)
        })], unpaired);
    }

    /// <summary>
    /// Takes out of the value changes <see cref="SeparatePairs"/> left unpaired each single-valued attribute that is both
    /// deleted from and added to, as one change (a delete, then an add), and returns what is left in the order it stood.
    /// Where any of the attribute's deletes names no value, the change deletes the attribute whole.
    /// </summary>
    /// <remarks>
    /// Defence in depth since #2182, not a path JIM's own exports take: export staging gives a single-valued attribute
    /// one Update, sent as a Replace, and an export carrying two changes for one is refused before it reaches any
    /// Connector. It stays so that a modify which does carry both is still applied in the one safe order.
    /// </remarks>
    private static (List<DirectoryAttributeModification[]> Changes, List<DirectoryAttributeModification> Remaining) SeparateSingleValuedChanges(
        List<DirectoryAttributeModification> unpaired,
        IReadOnlySet<string> singleValuedAttributes)
    {
        var changes = unpaired
            .Where(modification => singleValuedAttributes.Contains(modification.Name))
            .GroupBy(modification => modification.Name, StringComparer.OrdinalIgnoreCase)
            .Select(attributeChanges => (
                Name: attributeChanges.Key,
                Deletes: attributeChanges.Where(modification => modification.Operation == DirectoryAttributeOperation.Delete).ToList(),
                Adds: attributeChanges.Where(modification => modification.Operation == DirectoryAttributeOperation.Add).ToList()))
            .Where(change => change.Deletes.Count > 0 && change.Adds.Count > 0)
            .ToList();

        if (changes.Count == 0)
            return ([], unpaired);

        var changedAttributes = changes.Select(change => change.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ([.. changes.Select(change => new[]
        {
            NewModification(change.Name, DirectoryAttributeOperation.Delete,
                change.Deletes.Any(delete => delete.Count == 0) ? [] : change.Deletes.SelectMany(Values)),
            NewModification(change.Name, DirectoryAttributeOperation.Add, change.Adds.SelectMany(Values))
        })], [.. unpaired.Where(modification => !changedAttributes.Contains(modification.Name))]);
    }

    private static IEnumerable<Step> Parts(
        List<DirectoryAttributeModification> modifications,
        DirectoryAttributeOperation operation,
        bool objectClasses,
        bool answeredAsAWhole) =>
        modifications
            .Where(modification => modification.Operation == operation && IsObjectClass(modification) == objectClasses)
            .Select(modification => new Step([modification], SentAgain: false, answeredAsAWhole));

    private static IEnumerable<Step> Replacements(List<DirectoryAttributeModification> modifications)
    {
        var replacements = modifications.Where(modification => modification.Operation == DirectoryAttributeOperation.Replace).ToArray();
        return replacements.Length == 0 ? [] : [new Step(replacements, SentAgain: false, AnsweredAsAWhole: false)];
    }

    private static bool IsValueChange(DirectoryAttributeModification modification) =>
        modification.Operation is DirectoryAttributeOperation.Add or DirectoryAttributeOperation.Delete;

    private static bool IsObjectClass(DirectoryAttributeModification modification) =>
        modification.Name.Equals(LdapConnectorConstants.ObjectClassAttributeName, StringComparison.OrdinalIgnoreCase);

    // A delete naming no values removes the attribute whole, and is one change.
    private static int ValueCount(DirectoryAttributeModification modification) => Math.Max(modification.Count, 1);

    private static IEnumerable<object?> Values(DirectoryAttribute attribute) =>
        Enumerable.Range(0, attribute.Count).Select(i => attribute[i]);

    private static DirectoryAttributeModification NewModification(string name, DirectoryAttributeOperation operation, IEnumerable<object?> values)
    {
        var modification = new DirectoryAttributeModification { Name = name, Operation = operation };
        foreach (var value in values)
            AddValue(modification, value);
        return modification;
    }

    private static DirectoryAttributeModification Slice(DirectoryAttributeModification modification, int start, int end) =>
        NewModification(modification.Name, modification.Operation, Values(modification).Skip(start).Take(end - start));

    private static DirectoryAttributeModification CopyOf(DirectoryAttributeModification modification) =>
        Slice(modification, 0, modification.Count);

    private static void AddValue(DirectoryAttribute attribute, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case byte[] bytes:
                attribute.Add(bytes);
                break;
            case Uri uri:
                attribute.Add(uri);
                break;
            default:
                attribute.Add(value.ToString());
                break;
        }
    }

    /// <summary>
    /// One request of the plan.
    /// </summary>
    /// <param name="Modifications">What it carries: every replacement together, one attribute's adds or deletes, or one
    /// attribute's pairs or single-valued change (a delete, then an add).</param>
    /// <param name="SentAgain">Whether it carries adds held back until the deletes of their attribute had gone.</param>
    /// <param name="AnsweredAsAWhole">Whether the refused modify carried exactly this, so the directory has already
    /// refused it.</param>
    /// <param name="IsPair">Whether it carries one attribute's values deleted and added again under the same key.</param>
    /// <param name="IsSingleValuedChange">Whether it carries a single-valued attribute's deletes and adds, changing its
    /// value from the old to the new.</param>
    private sealed record Step(
        DirectoryAttributeModification[] Modifications,
        bool SentAgain,
        bool AnsweredAsAWhole,
        bool IsPair = false,
        bool IsSingleValuedChange = false)
    {
        /// <summary>
        /// Whether it carries a delete followed by an add that must never reach the directory ahead of it.
        /// </summary>
        public bool DeletesThenAdds => IsPair || IsSingleValuedChange;

        /// <summary>
        /// The one attribute's adds or deletes this step carries, or null for the replacements, for pairs and for a
        /// single-valued change.
        /// </summary>
        public DirectoryAttributeModification? ValueGroup =>
            Modifications is [{ Operation: DirectoryAttributeOperation.Add or DirectoryAttributeOperation.Delete } only] ? only : null;
    }
}
