// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Summary-tier identity snapshot of a Connected System Object (its external ID and object type name),
/// used to populate the historical display fields on Run Profile Execution Items without materialising
/// the object or its attribute values. Reference recall uses this to make each membership-removal RPEI
/// self-describing even if the referencing group's CSO is later deleted. The display name is not
/// projected here (it would require scanning a group's member attribute values); recall carries the
/// referencing Metaverse Object's display name instead.
/// </summary>
public class ConnectedSystemObjectDisplaySnapshot
{
    public Guid ConnectedSystemObjectId { get; set; }

    public string? ExternalId { get; set; }

    public string? TypeName { get; set; }
}
