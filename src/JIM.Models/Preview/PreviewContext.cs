// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// Everything an adapter needs to answer "what would this change do?": which object is being changed, what is being
/// proposed for it, and who is asking.
///
/// The proposed configuration is carried as an **unsaved** object, reusing whatever type the surface's own update
/// path already takes. That is the whole point of a preview: the change has not been made, and must not be, so an
/// adapter that resolved the proposal by re-reading the object from the database would evaluate the current
/// configuration and report that nothing would change.
/// </summary>
public class PreviewContext
{
    /// <summary>The surface being previewed; selects the adapter.</summary>
    public required ConfigurationChangePreviewSurface Surface { get; init; }

    /// <summary>
    /// The Activity that tracks this preview run. Adapters do not write to it (the orchestrator owns progress), but
    /// they need its id to attribute anything they persist.
    /// </summary>
    public required Guid ActivityId { get; init; }

    /// <summary>
    /// The integer identifier of the configuration object being changed, for the surfaces keyed that way
    /// (Synchronisation Rule, Connected System, Metaverse Object Type, Metaverse Attribute).
    /// </summary>
    public int? TargetId { get; init; }

    /// <summary>
    /// The Guid identifier of the configuration object, for surfaces keyed that way. Exactly one of this and
    /// <see cref="TargetId"/> is populated.
    /// </summary>
    public Guid? TargetGuidId { get; init; }

    /// <summary>
    /// The proposed configuration, as the surface's own update type. Adapters cast it to the type they expect and
    /// fail loudly if it is something else: a preview evaluating the wrong shape would answer confidently about a
    /// change nobody proposed.
    /// </summary>
    public required object ProposedConfiguration { get; init; }

    /// <summary>The principal who asked for the preview, recorded on the Activity by the orchestrator.</summary>
    public ActivityInitiatorType InitiatedByType { get; init; } = ActivityInitiatorType.NotSet;

    public Guid? InitiatedById { get; init; }

    public string? InitiatedByName { get; init; }

    /// <summary>
    /// The proposed configuration as <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The proposal is a different type. Thrown rather than returning null so a mismatch surfaces as a failed
    /// preview naming both types, instead of an empty result that reads as "nothing would change".
    /// </exception>
    public T ProposedAs<T>() where T : class =>
        ProposedConfiguration as T ??
        throw new InvalidOperationException(
            $"Preview of {Surface} was given a proposed configuration of type " +
            $"{ProposedConfiguration.GetType().Name}, but the adapter expects {typeof(T).Name}.");
}
