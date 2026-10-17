// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One member of a Group in the SCIM store. Members are rows rather than part of the Group's payload, so adding or
/// removing one member never rewrites a large Group.
/// </summary>
public class ScimStoreGroupMember
{
    /// <summary>
    /// The Group.
    /// </summary>
    public Guid GroupId { get; set; }

    /// <summary>
    /// The member: a User or another Group in the same Connected System's store.
    /// </summary>
    public Guid MemberId { get; set; }
}
