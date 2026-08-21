// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Staging;

namespace JIM.Models.Preview;

/// <summary>
/// The schema selection an administrator is proposing for a Connected System (#1475, #827 gap G6): which Object
/// Types JIM manages, which of their attributes it imports, and whether obsoleting an object of each type withdraws
/// the Metaverse values it contributed.
///
/// The whole selection is carried rather than the flags that moved, for the reason the partition and container
/// preview already documents: what a deselection costs depends on the rest of the selection. An attribute stops
/// being refreshed only if nothing else still brings it in, and an Object Type's objects are only stranded if the
/// type is genuinely leaving management rather than being swapped for another.
/// </summary>
/// <remarks>
/// A dedicated shape rather than the entity graph, for the reason every proposal has one: a proposal may be
/// evaluated in JIM.Worker, so it has to survive a JSON round trip, and
/// <see cref="ConnectedSystemObjectType"/> carries its whole attribute collection, its Object Matching Rules, and a
/// backlink to the Connected System.
/// </remarks>
/// <param name="ObjectTypes">Every Object Type the Connected System has discovered, selected or not. Deselected
/// types are carried too: a proposal that listed only the selected ones could not tell "this type is being
/// deselected" from "this type was never in the payload".</param>
public record ConnectedSystemSchemaProposal(IReadOnlyList<ConnectedSystemObjectTypeSelectionProposal> ObjectTypes)
{
    /// <summary>
    /// The schema selection currently in force, as a proposal. What "no change" looks like, and the baseline an
    /// adapter evaluates a proposal against.
    /// </summary>
    public static ConnectedSystemSchemaProposal FromCurrentConfiguration(
        IReadOnlyCollection<ConnectedSystemObjectType> objectTypes)
    {
        ArgumentNullException.ThrowIfNull(objectTypes);

        return new ConnectedSystemSchemaProposal(
            objectTypes.Select(ConnectedSystemObjectTypeSelectionProposal.FromObjectType).ToList());
    }

    /// <summary>
    /// The proposed selection for one Object Type, or null where the proposal says nothing about it. A caller that
    /// proposes a partial payload leaves the rest of the schema alone rather than deselecting it by omission.
    /// </summary>
    public ConnectedSystemObjectTypeSelectionProposal? For(int objectTypeId) =>
        ObjectTypes.FirstOrDefault(objectType => objectType.ObjectTypeId == objectTypeId);

    /// <summary>
    /// Whether this proposal and <paramref name="other"/> describe the same selection. Used to answer "nothing would
    /// change" before any object is evaluated, which is the cheapest honest answer a preview can give.
    /// </summary>
    public bool DescribesSameSchemaAs(ConnectedSystemSchemaProposal? other) =>
        other is not null && CanonicalKey() == other.CanonicalKey();

    /// <summary>
    /// A stable rendering of the whole selection, ordered by Object Type id so two proposals built from differently
    /// ordered payloads compare equal. Order carries no meaning here, unlike the Object Matching proposal, where a
    /// rule's position decides which rule wins.
    /// </summary>
    private string CanonicalKey() =>
        string.Join("|", ObjectTypes
            .OrderBy(objectType => objectType.ObjectTypeId)
            .Select(objectType => objectType.CanonicalKey()));
}

/// <summary>
/// One Object Type's proposed selection: whether JIM manages it, which of its attributes it imports, and what
/// obsoletion does to the values its objects contributed.
/// </summary>
/// <param name="ObjectTypeId">The Connected System Object Type this proposal is for.</param>
/// <param name="Name">Its name at the time the proposal was made, so a preview reads as the administrator's own
/// vocabulary rather than an id, and still does after the type is renamed or removed.</param>
/// <param name="Selected">Whether JIM manages this Object Type at all.</param>
/// <param name="RemoveContributedAttributesOnObsoletion">Whether obsoleting one of its objects withdraws the
/// Metaverse attribute values that object contributed.</param>
/// <param name="SelectedAttributeIds">The attributes JIM imports for this Object Type. A set, not a sequence:
/// selection order means nothing to any Connector.</param>
public record ConnectedSystemObjectTypeSelectionProposal(
    int ObjectTypeId,
    string Name,
    bool Selected,
    bool RemoveContributedAttributesOnObsoletion,
    IReadOnlyList<int> SelectedAttributeIds)
{
    /// <summary>
    /// The selection currently in force on <paramref name="objectType"/>.
    /// </summary>
    public static ConnectedSystemObjectTypeSelectionProposal FromObjectType(ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        // External IDs are selected implicitly and cannot be deselected, so they are read off the attribute's own
        // flags rather than trusted to the Selected column. An anchor left out of the baseline would then appear as
        // an attribute the proposal newly selects, on a proposal that changed nothing.
        var selectedAttributeIds = (objectType.Attributes ?? [])
            .Where(attribute => attribute.Selected || attribute.IsExternalId || attribute.IsSecondaryExternalId)
            .Select(attribute => attribute.Id)
            .Distinct()
            .ToList();

        return new ConnectedSystemObjectTypeSelectionProposal(
            objectType.Id,
            objectType.Name,
            objectType.Selected,
            objectType.RemoveContributedAttributesOnObsoletion,
            selectedAttributeIds);
    }

    /// <summary>
    /// Whether this proposal and <paramref name="other"/> describe the same selection for the Object Type. The name
    /// is deliberately excluded: it is display material carried for the preview's benefit, and a type renamed in the
    /// same save changes nothing about what synchronisation does with it.
    /// </summary>
    public bool DescribesSameSelectionAs(ConnectedSystemObjectTypeSelectionProposal? other) =>
        other is not null && CanonicalKey() == other.CanonicalKey();

    /// <summary>
    /// The attributes this proposal selects that <paramref name="other"/> did not. Empty where the proposal takes
    /// attributes away rather than adding them.
    /// </summary>
    public IReadOnlyList<int> AttributesSelectedBeyond(ConnectedSystemObjectTypeSelectionProposal? other) =>
        SelectedAttributeIds.Except(other?.SelectedAttributeIds ?? []).Order().ToList();

    /// <summary>
    /// The attributes <paramref name="other"/> selected that this proposal does not. These are the attributes that
    /// would stop being refreshed: the values already imported for them stay on the Connected System Object.
    /// </summary>
    public IReadOnlyList<int> AttributesDeselectedFrom(ConnectedSystemObjectTypeSelectionProposal? other) =>
        (other?.SelectedAttributeIds ?? []).Except(SelectedAttributeIds).Order().ToList();

    internal string CanonicalKey() =>
        string.Join(",",
            ObjectTypeId.ToString(CultureInfo.InvariantCulture),
            Selected ? "1" : "0",
            RemoveContributedAttributesOnObsoletion ? "1" : "0",
            string.Join("+", SelectedAttributeIds.Order().Select(id => id.ToString(CultureInfo.InvariantCulture))));
}
