// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The order in which a synchronisation pages through a Connected System's Object Types (#2492): every type comes
/// after the types its mapped reference attributes can point at, so references between types resolve in the main
/// page loop rather than being deferred to the cross-page reference step.
/// </summary>
/// <remarks>
/// Types that reference one another (a cycle, a type referencing itself included) cannot be ordered among
/// themselves; they form one group, kept together, and the references within it are what the cross-page step
/// exists for.
/// </remarks>
public sealed class SyncObjectTypeOrder
{
    /// <summary>
    /// Creates an order from its groups, each a set of Object Type ids, in paging order.
    /// </summary>
    public SyncObjectTypeOrder(IReadOnlyList<IReadOnlyList<int>> groups, IReadOnlySet<int> selfReferencingTypeIds)
    {
        Groups = groups;
        SelfReferencingTypeIds = selfReferencingTypeIds;
        TypeIds = groups.SelectMany(g => g).ToList();
    }

    /// <summary>
    /// The groups in paging order. A group of more than one type is a reference cycle; its types are listed by id.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> Groups { get; }

    /// <summary>
    /// Every Object Type id in paging order.
    /// </summary>
    public IReadOnlyList<int> TypeIds { get; }

    /// <summary>
    /// The types with a mapped reference attribute that can point at their own type (a manager, a nested group).
    /// </summary>
    public IReadOnlySet<int> SelfReferencingTypeIds { get; }

    /// <summary>
    /// Describes the order for a log line, e.g. "person, then group" or "person, then (group, contact)".
    /// </summary>
    public string Describe(Func<int, string> nameOf)
    {
        return string.Join(", then ", Groups.Select(g => g.Count == 1
            ? nameOf(g[0])
            : "(" + string.Join(", ", g.Select(nameOf)) + ")"));
    }
}
