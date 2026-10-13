// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;
using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// The object a causal timeline is about, live or deleted.
/// </summary>
public class CausalObjectHeaderDto
{
    /// <summary>
    /// Whether the object is a Connected System Object or a Metaverse Object.
    /// </summary>
    public CausalTimelineSubjectKind Kind { get; set; }

    /// <summary>
    /// The object's id, which its records keep after the object itself is deleted.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Whether the object has been deleted, so what is known about it comes from its deletion record.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// When the object was deleted (UTC), or null while it exists.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// The object's name, where it has one.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// A Connected System Object's external ID.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The object's type.
    /// </summary>
    public string? ObjectTypeName { get; set; }

    /// <summary>
    /// A Metaverse Object Type's plural name.
    /// </summary>
    public string? ObjectTypePluralName { get; set; }

    /// <summary>
    /// A Connected System Object's Connected System.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// Maps an object header to its API shape.
    /// </summary>
    public static CausalObjectHeaderDto FromModel(CausalObjectHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return new CausalObjectHeaderDto
        {
            Kind = header.Subject.Kind,
            Id = header.Subject.Id,
            IsDeleted = header.IsDeleted,
            DeletedAt = header.DeletedAt,
            DisplayName = header.DisplayName,
            ExternalId = header.ExternalId,
            ObjectTypeName = header.ObjectTypeName,
            ObjectTypePluralName = header.ObjectTypePluralName,
            ConnectedSystemId = header.ConnectedSystemId,
            ConnectedSystemName = header.ConnectedSystemName
        };
    }
}
