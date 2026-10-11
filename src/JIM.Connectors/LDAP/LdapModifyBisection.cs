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
/// checks after each. So object classes being added go first and those being removed go last, every replacement goes
/// together in one request, and each attribute's values are added before any are deleted, so an attribute an entry
/// must hold (groupOfNames' member) never passes through empty on its way from the old values to the new.
/// </para>
/// <para>
/// <b>Halving.</b> Each attribute's adds, and separately its deletes, are sent as one request. A part the directory
/// refuses as already in place is split in two and each half sent; only a half refused again is split further. One
/// value refused on its own is in place. For k such values among n, that is about 2k log2(n) requests, where sending
/// every value alone took n: on OpenLDAP every accepted modify of a large group rewrites the entry and, with the
/// accesslog's logold, logs every old member, so one stale member among a thousand used to cost 999 heavy writes.
/// A refused modify that carried nothing but one attribute's adds or deletes has already been answered for that part
/// as a whole, so it is split straight away rather than sent again.
/// </para>
/// <para>
/// <b>An add refused as already present is not always done.</b> The directory compares values with its own matching
/// rule: <c>smtp:jane.doe@x</c> is "already present" in a proxyAddresses holding <c>SMTP:jane.doe@x</c>, and if the
/// same export deletes <c>SMTP:jane.doe@x</c>, counting the add as done would lose the address. So while the refused
/// modify also deletes values of that attribute, such an add is held back and sent again after those deletes, and is
/// done only if the directory refuses it as already present once more. And where the Connected System's schema says
/// the attribute is single-valued (a multi-valued Metaverse attribute flowing to it, #435), Active Directory and
/// Samba AD refuse an add with the same answer when the attribute holds any other value, so a refused add there says
/// nothing about which value is held and is never treated as done: the export fails, as it did before the fallback
/// existed. A delete refused as already absent is always done.
/// </para>
/// </remarks>
internal sealed class LdapModifyBisection
{
    private readonly string _dn;
    private readonly IReadOnlySet<string> _singleValuedAttributes;
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
    /// <param name="meansAlreadyInDesiredState">Whether a refusal says the value of a modification is already where the
    /// export wants it; the directory's own codes differ.</param>
    internal LdapModifyBisection(
        ModifyRequest refused,
        IReadOnlySet<string> singleValuedAttributes,
        Func<DirectoryOperationException, DirectoryAttributeModification, bool> meansAlreadyInDesiredState)
    {
        _dn = refused.DistinguishedName;
        _singleValuedAttributes = singleValuedAttributes;
        _meansAlreadyInDesiredState = meansAlreadyInDesiredState;

        var modifications = refused.Modifications.Cast<DirectoryAttributeModification>().ToList();
        var valueChanges = modifications.Where(IsValueChange).ToList();
        ValueChanges = valueChanges.Sum(ValueCount);
        _attributesWithDeletes = valueChanges
            .Where(modification => modification.Operation == DirectoryAttributeOperation.Delete)
            .Select(modification => modification.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var answeredAsAWhole = modifications.Count == 1;
        _phases = new Queue<Func<IEnumerable<Step>>>(
        [
            () => Parts(modifications, DirectoryAttributeOperation.Add, objectClasses: true, answeredAsAWhole),
            () => Replacements(modifications),
            () => Parts(modifications, DirectoryAttributeOperation.Add, objectClasses: false, answeredAsAWhole),
            () => Parts(modifications, DirectoryAttributeOperation.Delete, objectClasses: false, answeredAsAWhole),
            () => HeldBack(objectClasses: false),
            () => Parts(modifications, DirectoryAttributeOperation.Delete, objectClasses: true, answeredAsAWhole),
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
    /// carried one attribute's adds or deletes, the directory says a value among them is already in place, and, for a
    /// lone add, the rules for adds (see the remarks on this class) allow it. Changes nothing, so it is safe in an
    /// exception filter; a refusal it does not accept fails the export.
    /// </summary>
    internal bool IsAlreadyInDesiredState(DirectoryOperationException refusal)
    {
        if (_current is not { ValueGroup: { } part } step || !_meansAlreadyInDesiredState(refusal, part))
            return false;

        return part.Count > 1 || MustBeSentAgainAfterTheDeletes(step, part) || !MayHoldAnotherValue(part);
    }

    /// <summary>
    /// The directory refused the request <see cref="TryTakeNext"/> gave in a way <see cref="IsAlreadyInDesiredState"/>
    /// accepted: halve it, hold back a lone add to send again after the deletes, or count a lone value as in place.
    /// </summary>
    internal void RefusedAsAlreadyInDesiredState()
    {
        var step = TakeCurrent();
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

    // An add refused as present before the deletes of its attribute have been sent: the value present may be one of
    // those the deletes remove, compared as the directory compares it, so it is sent again once they have gone.
    private bool MustBeSentAgainAfterTheDeletes(Step step, DirectoryAttributeModification part) =>
        part.Operation == DirectoryAttributeOperation.Add && !step.SentAgain && _attributesWithDeletes.Contains(part.Name);

    // A single-valued attribute refuses an add as present whenever it holds a value, whichever value that is.
    private bool MayHoldAnotherValue(DirectoryAttributeModification part) =>
        part.Operation == DirectoryAttributeOperation.Add && _singleValuedAttributes.Contains(part.Name);

    private void SplitIntoHalves(Step step)
    {
        var part = step.ValueGroup!;
        var half = part.Count / 2;
        _queue.AddFirst(new Step([Slice(part, half, part.Count)], step.SentAgain, AnsweredAsAWhole: false));
        _queue.AddFirst(new Step([Slice(part, 0, half)], step.SentAgain, AnsweredAsAWhole: false));
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

    private static DirectoryAttributeModification Slice(DirectoryAttributeModification modification, int start, int end)
    {
        var slice = new DirectoryAttributeModification { Name = modification.Name, Operation = modification.Operation };
        for (var i = start; i < end; i++)
            AddValue(slice, modification[i]);
        return slice;
    }

    private static DirectoryAttributeModification CopyOf(DirectoryAttributeModification modification) =>
        Slice(modification, 0, modification.Count);

    private static void AddValue(DirectoryAttribute attribute, object value)
    {
        switch (value)
        {
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
    /// <param name="Modifications">What it carries: every replacement together, or one attribute's adds or deletes.</param>
    /// <param name="SentAgain">Whether it carries adds held back until the deletes of their attribute had gone.</param>
    /// <param name="AnsweredAsAWhole">Whether the refused modify carried exactly this, so the directory has already
    /// refused it.</param>
    private sealed record Step(DirectoryAttributeModification[] Modifications, bool SentAgain, bool AnsweredAsAWhole)
    {
        /// <summary>
        /// The one attribute's adds or deletes this step carries, or null for the replacements.
        /// </summary>
        public DirectoryAttributeModification? ValueGroup =>
            Modifications is [{ Operation: DirectoryAttributeOperation.Add or DirectoryAttributeOperation.Delete } only] ? only : null;
    }
}
