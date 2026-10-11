// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// What diagnosing one Connected System Object's unresolved references needs, prepared once and reused for every
/// window and summary read about that object (#959): the classifier built from its Connected System's Container
/// Scope, and which of its attributes could hold a value ambiguous across Object Types.
/// </summary>
/// <remarks>
/// Holds no repository: each read takes the <see cref="JimApplication"/> of its own unit of work, so a page can keep
/// this for its lifetime without holding a disposed application.
/// </remarks>
public sealed class ConnectedSystemObjectUnresolvedReferenceContext
{
    internal ConnectedSystemObjectUnresolvedReferenceContext(
        int connectedSystemId,
        string connectedSystemName,
        UnresolvedReferenceHandling unresolvedReferenceHandling,
        Guid connectedSystemObjectId,
        string? objectTypeName,
        UnresolvedReferenceClassifier classifier,
        IReadOnlySet<int> ambiguityCandidateAttributeIds,
        IReadOnlyList<(int AnchorAttributeId, string ObjectTypeName)> ambiguityTargets)
    {
        ConnectedSystemId = connectedSystemId;
        ConnectedSystemName = connectedSystemName;
        UnresolvedReferenceHandling = unresolvedReferenceHandling;
        ConnectedSystemObjectId = connectedSystemObjectId;
        ObjectTypeName = objectTypeName;
        Classifier = classifier;
        AmbiguityCandidateAttributeIds = ambiguityCandidateAttributeIds;
        AmbiguityTargets = ambiguityTargets;
    }

    /// <summary>The Connected System the object belongs to.</summary>
    public int ConnectedSystemId { get; }

    /// <summary>The Connected System's name.</summary>
    public string ConnectedSystemName { get; }

    /// <summary>
    /// The Connected System's Unresolved Reference Handling as it stands now, which is not necessarily what it was when
    /// any one import ran.
    /// </summary>
    public UnresolvedReferenceHandling UnresolvedReferenceHandling { get; }

    /// <summary>The object whose unresolved references are diagnosed.</summary>
    public Guid ConnectedSystemObjectId { get; }

    /// <summary>The name of the object's Object Type, or null where the Connected System no longer holds the type.</summary>
    public string? ObjectTypeName { get; }

    internal UnresolvedReferenceClassifier Classifier { get; }

    /// <summary>
    /// The object's attributes whose values are resolved against Object Type anchors without a declared target, and so
    /// can be ambiguous (#1285). Empty where none can: every Reference attribute declares its target, the object's
    /// references resolve by secondary external ID, or the Connected System has fewer than two anchored Object Types.
    /// </summary>
    internal IReadOnlySet<int> AmbiguityCandidateAttributeIds { get; }

    /// <summary>The Object Types a value on those attributes could name, each by its anchor attribute.</summary>
    internal IReadOnlyList<(int AnchorAttributeId, string ObjectTypeName)> AmbiguityTargets { get; }
}
