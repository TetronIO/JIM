// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Decides which attributes of an export batch's Connected System Objects are loaded in part (#2816), for the one load
/// export execution uses (<c>ISyncRepository.GetPendingExportsByIdsAsync</c>), in the database and in memory alike.
/// </summary>
/// <remarks>
/// <para>
/// A group's member attribute would otherwise bring every member into memory for the handful of members a Pending Export
/// changes: a 500,000-user leaver step loaded 400,000 to 500,000 members per AllStaff group to remove about a hundred.
/// What export execution reads of such an attribute is bounded by the changes: optimistic apply looks only for the rows
/// a change could match, and the LDAP Connector and the class membership check need only how many values are held, and
/// only up to what the changes could remove. So such an attribute is loaded with the rows the changes could match, found
/// by the questions reconciliation already asks the database (<see cref="ReconciliationProbeQuestion"/>, #2669), beside a
/// count capped above anything the changes could remove.
/// </para>
/// <para>
/// An attribute is a candidate only where it is a multi-valued reference attribute that is not an External ID, a
/// Secondary External ID, a naming attribute or a connector-defined attribute, since those are read beyond the changes.
/// It is loaded whole whenever a change cannot be put as a question the database answers exactly (a change replacing or
/// clearing the whole set, a reference awaiting resolution, a string naming no object, a non-ASCII string compared
/// ignoring case, a database folding case differently), and whenever it holds fewer values than the cap, so an
/// attribute the changes could empty is always whole. A whole load is always correct, only slower.
/// </para>
/// </remarks>
public static class ExportValueLoadPlanner
{
    /// <summary>
    /// How many values above the changes' own count an attribute must hold to be loaded in part.
    /// </summary>
    public const int CapMargin = 2;

    /// <summary>
    /// Whether an attribute is ever loaded in part: a multi-valued reference attribute that identifies, names and
    /// declares nothing.
    /// </summary>
    public static bool IsTrimmable(ConnectedSystemObjectTypeAttribute attribute) =>
        attribute is { Type: AttributeDataType.Reference, AttributePlurality: AttributePlurality.MultiValued } &&
        !attribute.IsExternalId && !attribute.IsSecondaryExternalId && !attribute.IsConnectorDefined &&
        ObjectNaming.ConnectedSystemNameRank(attribute.Name) < 0;

    /// <summary>
    /// The candidates of a batch: for each object, each attribute of its Object Type that <see cref="IsTrimmable"/>, with
    /// every change the batch's exports make to it put as one question, or the reason it cannot be.
    /// </summary>
    /// <param name="exports">The batch's Pending Exports, with their changes and objects (the object's id and Object Type
    /// are read, never its values).</param>
    /// <param name="attributesByObjectTypeId">The attributes of the objects' Object Types; those not trimmable are
    /// ignored.</param>
    /// <param name="databaseFoldsCaseLikeIdentifierKey">Whether the database's <c>lower()</c> folds case as JIM does
    /// (<c>ISyncRepository.DoesDatabaseFoldCaseLikeIdentifierKeyAsync</c>).</param>
    public static List<ExportValueTrimCandidate> PlanCandidates(IEnumerable<PendingExport> exports,
        IReadOnlyDictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>> attributesByObjectTypeId,
        bool databaseFoldsCaseLikeIdentifierKey)
    {
        var objectsWithTheirAttributes = exports
            .Where(pe => pe.ConnectedSystemObject != null)
            .GroupBy(pe => pe.ConnectedSystemObject!.Id)
            .Select(objectExports => (
                ObjectId: objectExports.Key,
                Changes: objectExports.SelectMany(pe => pe.AttributeValueChanges).ToList(),
                Attributes: attributesByObjectTypeId.GetValueOrDefault(objectExports.First().ConnectedSystemObject!.TypeId)))
            .Where(o => o.Attributes != null);

        return objectsWithTheirAttributes
            .SelectMany(o => o.Attributes!.Where(IsTrimmable).Select(attribute => PlanCandidate(o.ObjectId, attribute,
                o.Changes.Where(change => change.AttributeId == attribute.Id).ToList(), databaseFoldsCaseLikeIdentifierKey)))
            .ToList();
    }

    /// <summary>
    /// How each object's values were loaded, from the candidates and each one's held count capped at its
    /// <see cref="ExportValueTrimCandidate.Cap"/>: the attributes loaded in part, and those large enough to have been that
    /// were loaded whole. Objects with neither are absent, as every value they hold is loaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">A candidate has no count: its size cannot be judged.</exception>
    public static Dictionary<Guid, ExportValueLoad> BuildValueLoads(IEnumerable<ExportValueTrimCandidate> candidates,
        IReadOnlyDictionary<(Guid ConnectedSystemObjectId, int AttributeId), int> cappedValueCounts)
    {
        var partial = new Dictionary<Guid, Dictionary<int, ExportPartiallyLoadedAttribute>>();
        var loadedWhole = new Dictionary<Guid, Dictionary<int, ExportValueLoadFallbackReason>>();

        foreach (var candidate in candidates)
        {
            if (!cappedValueCounts.TryGetValue((candidate.ConnectedSystemObjectId, candidate.Attribute.Id), out var count))
            {
                throw new InvalidOperationException(
                    $"No value count was read for attribute {candidate.Attribute.Id} of Connected System Object {candidate.ConnectedSystemObjectId}, " +
                    "so whether it may be loaded in part cannot be judged.");
            }

            if (candidate.IsTrimmedAt(count))
            {
                GetOrAdd(partial, candidate.ConnectedSystemObjectId)[candidate.Attribute.Id] =
                    new ExportPartiallyLoadedAttribute(candidate.Attribute.Id, candidate.Attribute.Name, count);
            }
            else if (count >= candidate.Cap)
            {
                GetOrAdd(loadedWhole, candidate.ConnectedSystemObjectId)[candidate.Attribute.Id] = candidate.FallbackReason!.Value;
            }
        }

        return partial.Keys.Union(loadedWhole.Keys).ToDictionary(id => id, id => new ExportValueLoad
        {
            PartiallyLoadedAttributes = partial.TryGetValue(id, out var p) ? p : new Dictionary<int, ExportPartiallyLoadedAttribute>(),
            LoadedWholeAttributes = loadedWhole.TryGetValue(id, out var w) ? w : new Dictionary<int, ExportValueLoadFallbackReason>()
        });
    }

    private static Dictionary<int, T> GetOrAdd<T>(Dictionary<Guid, Dictionary<int, T>> byObject, Guid objectId)
    {
        if (!byObject.TryGetValue(objectId, out var byAttribute))
        {
            byAttribute = [];
            byObject[objectId] = byAttribute;
        }
        return byAttribute;
    }

    /// <summary>
    /// One attribute of one object: every change on it put as one question, as reconciliation puts one
    /// (<c>SyncEngine.TryPutAsQuestion</c>), or the reason the first change that cannot be put stops it.
    /// </summary>
    /// <remarks>
    /// Optimistic apply matches a change against held rows by <c>SyncEngine.ReferenceRowMatches</c>: a resolved row by
    /// the object it names when the change names one too, otherwise by its string, compared as the attribute compares
    /// identifiers. Each way of asking returns a superset of the rows that rule can match: the named objects' ids among
    /// resolved rows; the strings among unresolved rows only, exactly on a case-exact attribute and ignoring case
    /// otherwise, which is all a change naming an object can match by string. A change naming no object matches a resolved
    /// row by its string too, which no question asks (#2871), so it loads the attribute whole.
    /// </remarks>
    private static ExportValueTrimCandidate PlanCandidate(Guid objectId, ConnectedSystemObjectTypeAttribute attribute,
        List<PendingExportAttributeValueChange> changes, bool databaseFoldsCaseLikeIdentifierKey)
    {
        var cap = changes.Count + CapMargin;
        var referenceIds = new HashSet<Guid>();
        var exactValues = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveValues = new HashSet<string>(StringComparer.Ordinal);

        ExportValueTrimCandidate WholeBecause(ExportValueLoadFallbackReason reason) => new(objectId, attribute, cap, null, reason);

        foreach (var change in changes)
        {
            // Resolved by the deferred pass after the load, possibly to something the question would not have asked about.
            if (!string.IsNullOrEmpty(change.UnresolvedReferenceValue))
                return WholeBecause(ExportValueLoadFallbackReason.UnresolvedReference);

            if (change.ChangeType is PendingExportAttributeChangeType.Update or PendingExportAttributeChangeType.RemoveAll)
                return WholeBecause(ExportValueLoadFallbackReason.WholeSetChange);

            if (change.ChangeType is not (PendingExportAttributeChangeType.Add or PendingExportAttributeChangeType.Remove))
                return WholeBecause(ExportValueLoadFallbackReason.UnrecognisedChangeType);

            if (change.ResolvedReferenceCsoId is { } referenceId)
                referenceIds.Add(referenceId);

            if (string.IsNullOrEmpty(change.StringValue))
                continue;

            if (!change.ResolvedReferenceCsoId.HasValue)
                return WholeBecause(ExportValueLoadFallbackReason.NamesNoObject);

            if (attribute.CaseExact)
            {
                exactValues.Add(change.StringValue);
                continue;
            }

            if (!databaseFoldsCaseLikeIdentifierKey)
                return WholeBecause(ExportValueLoadFallbackReason.DatabaseFoldsCaseDifferently);
            if (!change.StringValue.All(character => character < 0x80))
                return WholeBecause(ExportValueLoadFallbackReason.NonAsciiValue);

            caseInsensitiveValues.Add(change.StringValue);
        }

        return new ExportValueTrimCandidate(objectId, attribute, cap, new ReconciliationProbeQuestion
        {
            ConnectedSystemObjectId = objectId,
            AttributeId = attribute.Id,
            ChangeIds = changes.Select(change => change.Id).ToHashSet(),
            ReferenceIds = referenceIds,
            ExactValues = exactValues,
            CaseInsensitiveValues = caseInsensitiveValues,
            NeedsCount = false
        }, null);
    }
}
