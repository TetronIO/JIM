// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

public class MetaverseObjectHeader
{
    public Guid Id { get; set; }

    public DateTime Created { get; set; }

    /// <summary>
    /// The singular name of the object type (e.g., "User", "Group").
    /// </summary>
    public string TypeName { get; set; } = null!;

    /// <summary>
    /// The plural name of the object type for URLs and list views (e.g., "Users", "Groups").
    /// </summary>
    public string TypePluralName { get; set; } = null!;

    public int TypeId { get; set; }

    public List<MetaverseObjectAttributeValue> AttributeValues { get; set; } = new();

    public MetaverseObjectStatus Status { get; set; }

    /// <summary>
    /// Performance cache of the Display Name attribute value, used for efficient sorting at scale.
    /// </summary>
    public string? CachedDisplayName { get; set; }

    /// <summary>
    /// Resolves identically to <see cref="MetaverseObject.Name"/>; see that property for the policy.
    /// </summary>
    public string? Name
    {
        get
        {
            if (AttributeValues.Count == 0)
                return CachedDisplayName;

            return ObjectNaming.MetaverseNameFrom(AttributeValues) ?? CachedDisplayName;
        }
    }

    /// <summary>
    /// Resolves identically to <see cref="MetaverseObject.NameOrId"/>; see that property for the policy.
    /// </summary>
    public string NameOrId => ObjectNaming.FirstPresent(Name) ?? Id.ToString();

    public MetaverseObjectAttributeValue? GetAttributeValue(string name)
    {
        return AttributeValues.SingleOrDefault(q => q.Attribute?.Name == name);
    }

    public bool HasAttributeValue(string name)
    {
        return AttributeValues.Any(q => q.Attribute?.Name == name);
    }
}