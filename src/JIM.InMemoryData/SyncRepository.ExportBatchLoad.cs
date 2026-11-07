// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Runtime.CompilerServices;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;

namespace JIM.InMemoryData;

/// <summary>
/// The export batch load, as the database's loads it (#2816): a large group's member attribute is loaded with only the
/// members the batch's changes name, beside how many it holds, so export execution runs against the same shape of object
/// in unit and workflow tests as in production.
/// </summary>
/// <remarks>
/// This store hands out the instances it holds, so an object loaded in part is handed out as a copy holding the loaded
/// values, and its Pending Export as a copy naming that object: the stored object keeps every value, as the database
/// does. What execution writes back through those copies reaches the stored instances, as it would reach the database:
/// a Pending Export's state (<see cref="StorePendingExport"/>), an object's new values
/// (<see cref="UpdateConnectedSystemObjectsWithNewAttributeValuesAsync"/>) and optimistic apply's delta
/// (<see cref="ApplyExportedAttributeValuesAsync"/>). A batch with nothing to load in part is handed out as it is stored.
/// </remarks>
public partial class SyncRepository
{
    private readonly ConditionalWeakTable<PendingExport, PendingExport> _leanExportOriginals = new();
    private readonly ConditionalWeakTable<ConnectedSystemObject, ConnectedSystemObject> _leanObjectOriginals = new();
    private readonly Dictionary<Guid, ConnectedSystemObject> _leanObjectOriginalsById = new();

    /// <summary>
    /// The batch as export execution is handed it: the stored instances, except that each object with an attribute
    /// loaded in part (<see cref="ExportValueLoadPlanner"/>) is a copy holding only the values the database's load would
    /// load, and its Pending Exports are copies naming that copy.
    /// </summary>
    private List<PendingExport> LoadForExport(List<PendingExport> exports)
    {
        var objects = exports
            .Where(pe => pe.ConnectedSystemObject != null)
            .Select(pe => pe.ConnectedSystemObject!)
            .DistinctBy(cso => cso.Id)
            .ToDictionary(cso => cso.Id);
        if (objects.Count == 0)
            return exports;

        var attributesByObjectTypeId = objects.Values
            .GroupBy(cso => cso.TypeId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ConnectedSystemObjectTypeAttribute>)KnownAttributes(group.Key, group, exports));
        var candidates = ExportValueLoadPlanner.PlanCandidates(exports, attributesByObjectTypeId, databaseFoldsCaseLikeIdentifierKey: true);
        var counts = candidates.ToDictionary(
            candidate => (candidate.ConnectedSystemObjectId, candidate.Attribute.Id),
            candidate => Math.Min(candidate.Cap, objects[candidate.ConnectedSystemObjectId].AttributeValues.Count(av => av.AttributeId == candidate.Attribute.Id)));
        var loads = ExportValueLoadPlanner.BuildValueLoads(candidates, counts)
            .Where(load => load.Value.PartiallyLoadedAttributes.Count > 0)
            .ToDictionary(load => load.Key, load => load.Value);
        if (loads.Count == 0)
            return exports;

        var copies = new Dictionary<Guid, ConnectedSystemObject>();
        foreach (var (objectId, original, load) in loads.Select(l => (l.Key, objects[l.Key], l.Value)))
        {
            var questions = candidates
                .Where(candidate => candidate.ConnectedSystemObjectId == objectId && load.PartiallyLoadedAttributes.ContainsKey(candidate.Attribute.Id))
                .ToDictionary(candidate => candidate.Attribute.Id, candidate => candidate.Question!);

            var copy = ShallowCopy(original);
            copy.AttributeValues = new(original.AttributeValues.Where(av =>
                !questions.TryGetValue(av.AttributeId, out var question) || CouldMatch(av, question)));
            load.ApplyTo(copy);
            _leanObjectOriginals.AddOrUpdate(copy, original);
            _leanObjectOriginalsById[objectId] = original;
            copies[objectId] = copy;
        }

        return exports.Select(pe =>
        {
            if (pe.ConnectedSystemObject == null || !copies.TryGetValue(pe.ConnectedSystemObject.Id, out var copy))
                return pe;

            var exportCopy = ShallowCopy(pe);
            exportCopy.ConnectedSystemObject = copy;
            _leanExportOriginals.AddOrUpdate(exportCopy, pe);
            return exportCopy;
        }).ToList();
    }

    /// <summary>
    /// Whether a held row is one the database's probe returns for the question (<c>ReconciliationProbeValuesSql</c>): by
    /// the object it names, by its string exactly, or ignoring case among unresolved rows.
    /// </summary>
    private static bool CouldMatch(ConnectedSystemObjectAttributeValue row, ReconciliationProbeQuestion question) =>
        (row.ReferenceValueId.HasValue && question.ReferenceIds.Contains(row.ReferenceValueId.Value)) ||
        (row.UnresolvedReferenceValue != null && question.ExactValues.Contains(row.UnresolvedReferenceValue)) ||
        (!row.ReferenceValueId.HasValue && row.UnresolvedReferenceValue != null && question.CaseInsensitiveValues
            .Any(value => IdentifierKey.For(value, caseExact: false) == IdentifierKey.For(row.UnresolvedReferenceValue, caseExact: false)));

    /// <summary>
    /// The attributes known for an Object Type: its seeded definition's, the objects' own Object Type's, and those of the
    /// values and changes in hand. A value whose attribute is not known is never loaded in part.
    /// </summary>
    private List<ConnectedSystemObjectTypeAttribute> KnownAttributes(int objectTypeId, IEnumerable<ConnectedSystemObject> objectsOfType,
        IEnumerable<PendingExport> exports)
    {
        var objectsOfTypeList = objectsOfType.ToList();
        var objectIds = objectsOfTypeList.Select(cso => cso.Id).ToHashSet();
        return (_objectTypes.TryGetValue(objectTypeId, out var seeded) ? seeded.Attributes ?? [] : [])
            .Concat(objectsOfTypeList.SelectMany(cso => cso.Type?.Attributes ?? []))
            .Concat(objectsOfTypeList.SelectMany(cso => cso.AttributeValues).Select(av => av.Attribute))
            .Concat(exports.Where(pe => pe.ConnectedSystemObject != null && objectIds.Contains(pe.ConnectedSystemObject.Id))
                .SelectMany(pe => pe.AttributeValueChanges).Select(change => change.Attribute))
            .Where(attribute => attribute != null)
            .DistinctBy(attribute => attribute.Id)
            .ToList();
    }

    /// <summary>
    /// Stores a Pending Export a writer hands back. A copy handed out by <see cref="LoadForExport"/> writes its state into
    /// the stored instance, which keeps naming the stored object: the store never comes to hold an object loaded in part.
    /// </summary>
    private void StorePendingExport(PendingExport pendingExport)
    {
        if (!_leanExportOriginals.TryGetValue(pendingExport, out var original))
        {
            _pendingExports[pendingExport.Id] = pendingExport;
            return;
        }

        foreach (var property in WritableProperties<PendingExport>().Where(p => p.Name != nameof(PendingExport.ConnectedSystemObject)))
            property.SetValue(original, property.GetValue(pendingExport));
        _pendingExports[original.Id] = original;
    }

    /// <summary>
    /// The stored object a copy handed out by <see cref="LoadForExport"/> stands for, or the instance itself.
    /// </summary>
    private ConnectedSystemObject OriginalOf(ConnectedSystemObject connectedSystemObject) =>
        _leanObjectOriginals.TryGetValue(connectedSystemObject, out var original) ? original : connectedSystemObject;

    /// <summary>
    /// The stored object behind a copy handed out by <see cref="LoadForExport"/>, for an object this store was never
    /// seeded with (a Pending Export carrying its own).
    /// </summary>
    private bool TryGetLeanOriginal(ConnectedSystemObject connectedSystemObject, out ConnectedSystemObject original)
    {
        if (_leanObjectOriginals.TryGetValue(connectedSystemObject, out var found))
        {
            original = found;
            return true;
        }

        original = null!;
        return false;
    }

    /// <summary>
    /// Applies optimistic apply's delta to the stored objects behind copies handed out by <see cref="LoadForExport"/>.
    /// An object handed out as stored is brought into line by export execution itself (D10), as before.
    /// </summary>
    private void ApplyExportedValuesToLeanOriginals(List<ConnectedSystemObjectAttributeValue> additions, List<Guid> removalValueIds,
        IReadOnlyCollection<Guid> affectedCsoIds)
    {
        var additionsToCopies = additions
            .Select(addition => (Addition: addition, Original: OriginalOf(addition.ConnectedSystemObject)))
            .Where(pair => !ReferenceEquals(pair.Original, pair.Addition.ConnectedSystemObject) && !pair.Original.AttributeValues.Contains(pair.Addition));
        foreach (var (addition, original) in additionsToCopies)
            original.AttributeValues.Add(addition);

        if (removalValueIds.Count == 0)
            return;

        var removals = removalValueIds.ToHashSet();
        foreach (var original in affectedCsoIds.Select(id => _leanObjectOriginalsById.GetValueOrDefault(id)).OfType<ConnectedSystemObject>())
            original.AttributeValues.RemoveAll(av => removals.Contains(av.Id));
    }

    private static T ShallowCopy<T>(T source) where T : new()
    {
        var copy = new T();
        foreach (var property in WritableProperties<T>())
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    private static IEnumerable<PropertyInfo> WritableProperties<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0 && p.SetMethod!.IsPublic);
}
