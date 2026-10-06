// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.UniqueValues;

/// <summary>
/// One object's request for a unique value on one attribute (Unique Value Generation, #242, FR 21). Callers
/// (the sync engine, Sync Preview, and in future a workflow step or administrator action) build this from
/// whatever they know; the service knows nothing about the caller, a synchronisation run, or a Synchronisation
/// Rule beyond the <see cref="Generation"/> settings themselves.
/// </summary>
public sealed record GenerationRequest
{
    /// <summary>
    /// Whether this request is for an import mapping (a Metaverse attribute) or an export mapping (a Connected
    /// System attribute). Determines which pair of object/attribute ids below applies.
    /// </summary>
    public required GeneratedValueMode Mode { get; init; }

    /// <summary>
    /// Import mode: the Metaverse Object the value is for. Null when the object has not yet been persisted
    /// (a brand new joiner still being staged); <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/>'s
    /// <c>objectIdResolver</c> supplies the real id once it exists.
    /// </summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>
    /// Import mode: the Metaverse attribute the value is generated for.
    /// </summary>
    public int? MetaverseAttributeId { get; init; }

    /// <summary>
    /// Export mode: the Connected System Object the value is for. Null when the object has not yet been
    /// persisted, for the same reason as <see cref="MetaverseObjectId"/>.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// Export mode: the Connected System Object Type attribute the value is generated for.
    /// </summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; init; }

    /// <summary>
    /// The generated mapping's settings: token kind, placement inputs, attempt limit and so on. Also the
    /// origin recorded on any assignment this request produces.
    /// </summary>
    public required SyncRuleMappingGeneration Generation { get; init; }

    /// <summary>
    /// The target attribute's data type. Only <see cref="AttributeDataType.Text"/>, <see cref="AttributeDataType.Number"/>
    /// and <see cref="AttributeDataType.LongNumber"/> are valid; every other value is a caller error.
    /// </summary>
    public required AttributeDataType TargetType { get; init; }

    /// <summary>
    /// The target attribute's display name, used in failure messages so an administrator can identify the
    /// attribute without cross-referencing an id.
    /// </summary>
    public required string AttributeName { get; init; }

    /// <summary>
    /// The evaluated base expression, or null when there is none (a sequence or random token with no base). An
    /// empty or whitespace-only value is treated the same as null by <see cref="GeneratedValueTokenKind.OnlyIfTaken"/>,
    /// which requires a base value (<see cref="GenerationOutcomeKind.NoBaseValue"/>).
    /// </summary>
    public string? BaseValue { get; init; }

    /// <summary>
    /// Import mode only: the Connected System Object Type attribute ids that export mappings flow this
    /// Metaverse attribute to (export mappings already excluded by the generation's own
    /// <see cref="SyncRuleMappingGenerationExclusion"/> list). Checked by the connector space gate. Empty for
    /// export mode, and for an import mapping with no participating export mapping.
    /// </summary>
    public IReadOnlyCollection<int> ConnectorSpaceAttributeIds { get; init; } = [];

    /// <summary>
    /// Import mode only: Connected System Objects that belong to this request's Metaverse Object in memory this
    /// pass, whether or not the join has been saved yet (typically the object being synchronised, which may
    /// have projected the Metaverse Object or joined it moments ago). The connector-space gate treats a value
    /// held by one of these as the person's own account, not a collision (#242: no different to an ordinary
    /// Attribute Flow). Saved joins are recognised without this, from the holder's own Metaverse Object id.
    /// </summary>
    public IReadOnlyCollection<Guid> OwnConnectedSystemObjectIds { get; init; } = [];

    /// <summary>
    /// Import mode only: a Connected System Object leaving this request's Metaverse Object in this pass (an
    /// obsoleting or out-of-scope object whose withdrawal re-elected the generated mapping). Its saved join still
    /// names the object, but it is no longer the person's account, so the connector-space gate counts its value
    /// as taken like anyone else's.
    /// </summary>
    public Guid? DisconnectingConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The Connected System attributes this request's candidates are probed against by the ProbeGate (release 3),
    /// when <see cref="UniqueValueResolveOptions.ProbeSession"/> is set: import mode, the participating targets the
    /// value flows to directly (<see cref="GeneratedValueParticipation.ComputeProbeTargets"/>); export mode, the
    /// generated attribute itself (<see cref="GeneratedValueParticipation.IsDirectProbeTarget"/>). Empty means
    /// nothing is probed.
    /// </summary>
    public IReadOnlyList<UniquenessProbeTarget> ProbeTargets { get; init; } = [];

    /// <summary>
    /// Values the ProbeGate accepts without probing, compared case-insensitively: import mode, the values the
    /// object's own joined accounts already hold for a probed attribute (the same person, not a collision, so a
    /// directory that finds that very account must not reject them); export mode, the object's current value for the
    /// target attribute.
    /// </summary>
    public IReadOnlyCollection<string> ProbeExemptValues { get; init; } = [];

    /// <summary>
    /// When true, the mapping's base expression could not be evaluated for this object (a required input is
    /// missing and the mapping's Missing Input Behaviour is "contribute no value", the default for a generated
    /// mapping per FR 29). <see cref="UniqueValueGenerationServer.ResolveAsync"/> then only checks for an
    /// existing sticky assignment; it never generates for this request, and returns
    /// <see cref="GenerationOutcomeKind.Waiting"/> when there is no sticky assignment to return.
    /// </summary>
    public bool StickyOnly { get; init; }

    /// <summary>
    /// Opaque caller state, returned unchanged on the resulting <see cref="GenerationOutcome"/> so the caller
    /// can correlate an outcome back to whatever it is processing (for example, the page item the request came
    /// from) without the service needing to know its shape.
    /// </summary>
    public object? CallerState { get; init; }
}
