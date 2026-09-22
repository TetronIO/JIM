// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The page-level context the Run Profile Execution Item detail page supplies to the causality
/// visualisation: the executing Connected System and Run Profile, the record (CSO) being processed
/// and the Connected System it belongs to, and the joined Metaverse Object Type names for link
/// building. All values are optional; legacy or partially-loaded data must degrade gracefully rather
/// than error.
///
/// Two distinct Connected System identities are carried, and they are NOT interchangeable:
/// <see cref="ConnectedSystemId"/>/<see cref="ConnectedSystemName"/> describe the run, while
/// <see cref="CsoConnectedSystemId"/>/<see cref="CsoConnectedSystemName"/> describe the record. The
/// two normally coincide, but diverge for cross-system cascades, e.g. a Full Synchronisation on
/// system A provisioning or exporting to a Connected System Object on system B: that Run Profile
/// Execution Item's run system is A, but its record's own system is B. Picking the wrong one either
/// mislabels the run or builds a link to a system the record does not live on (which 404s, since
/// Connected System Object lookups are scoped by both the Connected System id and the object id).
/// </summary>
/// <param name="ConnectedSystemId">
/// Id of the Connected System the Run Profile executed against (the Activity's system). Describes
/// the run, not the record: use for the causality summary's "on &lt;system&gt;" clause, the summary
/// band's run chip, and the Execution Summary panel's Connected System field. Do not use this to
/// build a link to the record or to label a record-owned event; use
/// <see cref="CsoConnectedSystemId"/> for those instead.
/// </param>
/// <param name="ConnectedSystemName">Name of the Connected System the run executed against (e.g. "HR CSV Source"); see <see cref="ConnectedSystemId"/>.</param>
/// <param name="RunProfileName">Name of the executed Run Profile (e.g. "Full Synchronisation").</param>
/// <param name="CsoId">Id of the processed Connected System Object; null when it has been deleted.</param>
/// <param name="CsoConnectedSystemId">
/// Id of the Connected System the record (<see cref="CsoId"/>) itself belongs to. Describes the
/// record, not the run: use for the record's own hyperlink, per-event Connected System badges for
/// outcomes that belong to the record's own system (CsoAdded, CsoUpdated, CsoDeleted,
/// DeletionDetected, Exported, ExportConfirmed, ExportFailed, Deprovisioned), and the ExportFailed
/// "Pending Exports" link. Null when unresolved; consumers must degrade to no link rather than link
/// to the wrong system.
/// </param>
/// <param name="CsoConnectedSystemName">Name of the Connected System the record belongs to; see <see cref="CsoConnectedSystemId"/>.</param>
/// <param name="CsoDisplayName">Display name of the record (e.g. "Liam Allen").</param>
/// <param name="CsoExternalId">External id of the record (e.g. "S8-287551").</param>
/// <param name="CsoObjectTypeName">The record's object type name (e.g. "person").</param>
/// <param name="MvoTypeName">Singular Metaverse Object Type name (e.g. "Person").</param>
/// <param name="MvoTypePluralName">Plural Metaverse Object Type name (e.g. "People") for link building.</param>
/// <param name="DeletedMetaverseObjectId">
/// The Identity's id where the page looked it up and it was not there, so the panel can say the object is
/// gone and offer its deletion record; null where the Identity is alive, or where nothing was looked up.
/// Distinct from an unbuildable link: this is evidence of deletion, not an inability to address something.
/// </param>
/// <param name="ConnectedSystemObjectNames">
/// The current display label (see <c>ConnectedSystemObject.NameOrId</c>-style resolution: display name,
/// else external id) of every Connected System Object a Provisioned outcome in this item's tree created,
/// keyed by the object's id. A recorded run's Provisioned/PendingExportCreated links are built from what
/// the run captured at the time, which for a newly-created object is only its internal id (it had no
/// external id yet); resolving the current label at view time keeps the Timeline and Table view from
/// permanently showing "person: &lt;guid&gt;" once the object has since been exported and named. Optional
/// and defaults to null so every existing caller compiles unchanged; a key missing from the map (the
/// object has since been deleted) simply falls back to the recorded label. Never populated for a
/// speculative Sync Preview, which creates nothing.
/// </param>
public sealed record CausalityPageContext(
    int? ConnectedSystemId,
    string? ConnectedSystemName,
    string? RunProfileName,
    Guid? CsoId,
    int? CsoConnectedSystemId,
    string? CsoConnectedSystemName,
    string? CsoDisplayName,
    string? CsoExternalId,
    string? CsoObjectTypeName,
    string? MvoTypeName,
    string? MvoTypePluralName,
    Guid? DeletedMetaverseObjectId = null,
    IReadOnlyDictionary<Guid, string>? ConnectedSystemObjectNames = null);
