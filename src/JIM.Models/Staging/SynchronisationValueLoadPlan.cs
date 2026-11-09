// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Which attributes a Delta Synchronisation's page load may leave out of an object (#2818): by Object Type, the
/// multi-valued reference attributes whose only reader is Drift Detection, which compares them in the database instead.
/// The load leaves one out of an object only where the object holds at least <see cref="MinimumHeldValueCount"/> values of
/// it and Drift Detection can be answered from the database at the moment it runs; everything else is loaded whole.
/// </summary>
public sealed class SynchronisationValueLoadPlan
{
    /// <summary>
    /// The threshold a run uses unless a test sets another: a group smaller than this costs less to load than to ask about.
    /// </summary>
    public const int DefaultMinimumHeldValueCount = 1_000;

    /// <summary>
    /// A plan that leaves nothing out.
    /// </summary>
    public static SynchronisationValueLoadPlan None { get; } = new(new Dictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>>(), int.MaxValue);

    public SynchronisationValueLoadPlan(IReadOnlyDictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>> attributesByObjectTypeId,
        int minimumHeldValueCount)
    {
        ArgumentNullException.ThrowIfNull(attributesByObjectTypeId);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumHeldValueCount);
        AttributesByObjectTypeId = attributesByObjectTypeId;
        MinimumHeldValueCount = minimumHeldValueCount;
    }

    /// <summary>
    /// The attributes that may be left out, by Object Type id.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>> AttributesByObjectTypeId { get; }

    /// <summary>
    /// How many values an object must hold of an attribute for the attribute to be left out.
    /// </summary>
    public int MinimumHeldValueCount { get; }

    /// <summary>
    /// Whether the plan leaves nothing out of any object.
    /// </summary>
    public bool LeavesNothingOut => AttributesByObjectTypeId.Count == 0 || MinimumHeldValueCount == int.MaxValue;
}
