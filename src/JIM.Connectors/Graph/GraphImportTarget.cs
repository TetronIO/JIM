// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Connectors.Graph;

/// <summary>
/// One Object Type to import, worked out from what the administrator selected: the values to read, the relationships to
/// fetch, and the <c>$select</c> that asks Graph for exactly those plus what <c>sourceOfAuthority</c> is derived from.
/// </summary>
internal sealed class GraphImportTarget
{
    private GraphImportTarget(string name, bool isUser)
    {
        Name = name;
        IsUser = isUser;
    }

    public string Name { get; }

    public bool IsUser { get; }

    /// <summary>The collection's path: <c>users</c> or <c>groups</c>.</summary>
    public string Collection => IsUser ? "users" : "groups";

    public List<GraphReadAttribute> Attributes { get; } = [];

    public bool Manager { get; private set; }

    public bool Members { get; private set; }

    public bool Owners { get; private set; }

    /// <summary>
    /// Whether the members of a group whose membership is hidden may be read: the token carries Member.Read.Hidden.
    /// Without it such a group's members are reported unreadable rather than asked for.
    /// </summary>
    public bool ReadsHiddenMembership { get; private set; }

    /// <summary>The properties every read of this Object Type selects.</summary>
    public List<string> Select { get; } = [];

    /// <summary>
    /// What the delta baseline selects: the same properties plus the relationships whose changes a Delta Import reads,
    /// since a delta round tracks only what its first request selected.
    /// </summary>
    public IEnumerable<string> DeltaSelect => Select
        .Concat(Manager ? [GraphSchemaCatalogue.Manager] : [])
        .Concat(Members ? [GraphSchemaCatalogue.Members] : [])
        .Concat(Owners ? [GraphSchemaCatalogue.Owners] : []);

    /// <summary>
    /// What a delta link for this Object Type was taken to track. A Delta Import whose target no longer matches the
    /// signature its link was taken with cannot read its changes: a round tracks only what its first request selected,
    /// and a hidden membership that could not be read before was never tracked either.
    /// </summary>
    public string Signature => string.Join(',', DeltaSelect) + (Members && ReadsHiddenMembership ? ";hiddenMembership" : "");

    /// <summary>
    /// The targets for a Connected System's selected Object Types, users before groups, so a position taken on one page
    /// means the same thing on the next. An Object Type whose attributes have never been listed has selected nothing,
    /// so everything the catalogue offers is read.
    /// </summary>
    public static List<GraphImportTarget> For(ConnectedSystem connectedSystem, bool readsHiddenMembership)
    {
        var selected = connectedSystem.ObjectTypes?.Where(o => o.Selected).ToList() ?? [];
        return new[] { (GraphSchemaCatalogue.UserObjectType, GraphSchemaCatalogue.UserAttributes), (GraphSchemaCatalogue.GroupObjectType, GraphSchemaCatalogue.GroupAttributes) }
            .Select(t => (t.Item1, t.Item2, ObjectType: selected.FirstOrDefault(o => string.Equals(o.Name, t.Item1, StringComparison.OrdinalIgnoreCase))))
            .Where(t => t.ObjectType != null)
            .Select(t => Build(t.Item1, t.Item2, t.ObjectType!, readsHiddenMembership))
            .ToList();
    }

    private static GraphImportTarget Build(string name, IReadOnlyList<GraphSchemaAttribute> catalogue, ConnectedSystemObjectType objectType, bool readsHiddenMembership)
    {
        var target = new GraphImportTarget(name, name == GraphSchemaCatalogue.UserObjectType) { ReadsHiddenMembership = readsHiddenMembership };
        var byName = catalogue.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var chosen = objectType.Attributes.Count == 0
            ? catalogue.Select(a => (a.Name, a.Type, a.Plurality)).ToList()
            : objectType.Attributes.Where(a => a.Selected).Select(a => (a.Name, a.Type, Plurality: a.AttributePlurality)).ToList();

        var select = new List<string> { GraphSchemaCatalogue.Id };
        foreach (var (attributeName, type, plurality) in chosen)
        {
            if (byName.TryGetValue(attributeName, out var known))
            {
                switch (known.Name)
                {
                    case GraphSchemaCatalogue.Manager:
                        target.Manager = true;
                        continue;
                    case GraphSchemaCatalogue.Members:
                        target.Members = true;
                        continue;
                    case GraphSchemaCatalogue.Owners:
                        target.Owners = true;
                        continue;
                }

                if (known.SelectProperty == null)
                    continue;

                target.Attributes.Add(new GraphReadAttribute(known.Name, type, plurality, known.SelectProperty, known.Member));
                select.Add(known.SelectProperty);
            }
            else if (attributeName.StartsWith("extension_", StringComparison.OrdinalIgnoreCase))
            {
                target.Attributes.Add(new GraphReadAttribute(attributeName, type, plurality, attributeName));
                select.Add(attributeName);
            }
        }

        select.AddRange(target.IsUser ? GraphSourceOfAuthority.UserProperties : GraphSourceOfAuthority.GroupProperties);

        // Whether a group's membership is hidden decides whether its members may be asked for.
        if (target.Members)
            select.Add(GraphSchemaCatalogue.Visibility);
        target.Select.AddRange(select.Distinct(StringComparer.Ordinal));
        return target;
    }
}
