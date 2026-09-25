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
    /// A value the caller found already sitting on the object for this attribute, if any (adopt before
    /// generate, FR 30). Import mode only, sourced from the Metaverse Object's own held value
    /// (<see cref="GeneratedValueParticipation.FindMetaverseOwnValue"/>): a joined Connected System Object's
    /// value is never a source here (product-owner decision; connector-space adoption sat outside the
    /// Attribute Flow priority model and has been removed). Export mode never sets this; with no assignment,
    /// generation always runs. Null or empty when there is nothing to adopt.
    /// </summary>
    public string? AdoptableValue { get; init; }

    /// <summary>
    /// Import mode only: the Connected System Object Type attribute ids that export mappings flow this
    /// Metaverse attribute to (export mappings already excluded by the generation's own
    /// <see cref="SyncRuleMappingGenerationExclusion"/> list). Checked by the connector space gate. Empty for
    /// export mode, and for an import mapping with no participating export mapping.
    /// </summary>
    public IReadOnlyCollection<int> ConnectorSpaceAttributeIds { get; init; } = [];

    /// <summary>
    /// When true, the mapping's base expression could not be evaluated for this object (a required input is
    /// missing and the mapping's Missing Input Behaviour is "contribute no value", the default for a generated
    /// mapping per FR 29). <see cref="UniqueValueGenerationServer.ResolveAsync"/> then only checks for an
    /// existing sticky assignment; it never adopts or generates for this request, and returns
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
