// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Scim;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request to create a key for a SCIM client to call a Connected System's SCIM endpoint.
/// </summary>
public class ScimKeyCreateRequestDto
{
    /// <summary>
    /// The key's name, typically naming the identity provider that will hold it.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What the key is for, if anything needs saying.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// When the key stops working. A year from now when omitted; at most two years.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Change reason must not exceed 1000 characters.")]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request to change a SCIM client's key. Only the values given change.
/// </summary>
public class ScimKeyUpdateRequestDto
{
    /// <summary>
    /// The key's new name; leave out to keep the current one.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The key's new description; leave out to keep the current one, or send an empty string to clear it.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// When the key stops working; leave out to keep the current expiry. At most two years from now.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Whether the key works; leave out to keep the current state. Disabling the old key is the last step of a
    /// rotation.
    /// </summary>
    public bool? IsEnabled { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Change reason must not exceed 1000 characters.")]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// A Connected System's SCIM endpoint and what its store holds.
/// </summary>
public class ScimServiceProviderSummaryDto
{
    /// <summary>
    /// The base URL to give an identity provider, as this request reached JIM.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// How many resources of each type the store holds, not counting deleted ones.
    /// </summary>
    public List<ScimResourceTypeCountDto> ResourceCounts { get; set; } = [];

    /// <summary>
    /// Resources a SCIM client has deleted, kept until an import has read the deletion.
    /// </summary>
    public int Tombstones { get; set; }

    /// <summary>
    /// The number of the last change written to the store; zero when nothing has been written.
    /// </summary>
    public long LastChangeSequence { get; set; }

    /// <summary>
    /// The change the last successful import read up to; null when the store has never been imported.
    /// </summary>
    public long? ImportedUpTo { get; set; }

    /// <summary>
    /// Resources changed since the last import, which the next import will read.
    /// </summary>
    public int ChangesAwaitingImport { get; set; }

    /// <summary>
    /// When a SCIM client last wrote to the store; null when none has.
    /// </summary>
    public DateTime? LastWrite { get; set; }

    /// <summary>
    /// Creates the DTO from a store summary and the endpoint's base URL.
    /// </summary>
    public static ScimServiceProviderSummaryDto FromSummary(ScimStoreSummary summary, string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        ResourceCounts = summary.ResourceCounts.Select(c => new ScimResourceTypeCountDto { ResourceTypeName = c.ResourceTypeName, Count = c.Count }).ToList(),
        Tombstones = summary.Tombstones,
        LastChangeSequence = summary.LastChangeSequence,
        ImportedUpTo = summary.ImportedUpTo,
        ChangesAwaitingImport = summary.ChangesAwaitingImport,
        LastWrite = summary.LastWrite
    };
}

/// <summary>
/// How many resources of one type a SCIM store holds.
/// </summary>
public class ScimResourceTypeCountDto
{
    /// <summary>
    /// The resource type, for example <c>User</c>.
    /// </summary>
    public string ResourceTypeName { get; set; } = string.Empty;

    /// <summary>
    /// How many resources of the type the store holds, not counting deleted ones.
    /// </summary>
    public int Count { get; set; }
}
