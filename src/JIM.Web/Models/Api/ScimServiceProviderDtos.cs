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
    /// Requests the endpoint refused in the last 24 hours, across every key, other than for authentication. Each key's
    /// SCIM request Activities name the latest rejection and count the rest.
    /// </summary>
    public int RejectedLast24Hours { get; set; }

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
        LastWrite = summary.LastWrite,
        RejectedLast24Hours = summary.RejectedLast24Hours
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

/// <summary>
/// One accepted write to a Connected System's SCIM store, as its journal records it: which resource, what the write
/// did, which attributes it changed or ignored (by name, never by value), and the key and address that made it.
/// </summary>
public class ScimJournalEntryDto
{
    /// <summary>
    /// The store's change number for the write; an import reads every write after the one it last read.
    /// </summary>
    public long ChangeSequence { get; set; }

    /// <summary>
    /// When the write was accepted.
    /// </summary>
    public DateTime Occurred { get; set; }

    /// <summary>
    /// The resource written, which is the Connected System Object's external id.
    /// </summary>
    public Guid ResourceId { get; set; }

    /// <summary>
    /// The resource's type, for example <c>User</c>.
    /// </summary>
    public string ResourceTypeName { get; set; } = string.Empty;

    /// <summary>
    /// What the write did: <c>Created</c>, <c>Replaced</c>, <c>Patched</c>, <c>Deleted</c>, or
    /// <c>ReferenceRemovedByDeletion</c> for a membership removed because the resource it pointed to was deleted.
    /// </summary>
    public ScimStoreOperation Operation { get; set; }

    /// <summary>
    /// The SCIM attributes the write changed, by name.
    /// </summary>
    public List<string> ChangedAttributes { get; set; } = [];

    /// <summary>
    /// Attributes the request sent that the store does not keep, by name.
    /// </summary>
    public List<string> IgnoredAttributes { get; set; } = [];

    /// <summary>
    /// The key the write was made with; empty once that key has been deleted.
    /// </summary>
    public Guid? ApiKeyId { get; set; }

    /// <summary>
    /// The key's name when the write was made.
    /// </summary>
    public string? ApiKeyName { get; set; }

    /// <summary>
    /// The address the request came from.
    /// </summary>
    public string? ClientAddress { get; set; }

    /// <summary>
    /// Creates the DTO from a journal entry.
    /// </summary>
    public static ScimJournalEntryDto FromEntity(ScimStoreJournalEntry entry) => new()
    {
        ChangeSequence = entry.ChangeSequence,
        Occurred = entry.Occurred,
        ResourceId = entry.ResourceId,
        ResourceTypeName = entry.ResourceTypeName,
        Operation = entry.Operation,
        ChangedAttributes = entry.ChangedAttributes,
        IgnoredAttributes = entry.IgnoredAttributes,
        ApiKeyId = entry.ApiKeyId,
        ApiKeyName = entry.ApiKeyName,
        ClientAddress = entry.ClientAddress
    };
}

/// <summary>
/// The SCIM requests behind one imported change to a Connected System Object: the resource's accepted writes between
/// the point its import started from and the point it read up to.
/// </summary>
public class ScimRequestsBehindChangeDto
{
    /// <summary>
    /// The resource, which is the Connected System Object's external id.
    /// </summary>
    public Guid ResourceId { get; set; }

    /// <summary>
    /// The change number the import started after; empty for an import that read from the start.
    /// </summary>
    public long? AfterSequence { get; set; }

    /// <summary>
    /// The change number the import read up to, inclusive.
    /// </summary>
    public long UpToSequence { get; set; }

    /// <summary>
    /// The resource's accepted writes in that range, oldest first.
    /// </summary>
    public List<ScimJournalEntryDto> Entries { get; set; } = [];

    /// <summary>
    /// Creates the DTO.
    /// </summary>
    public static ScimRequestsBehindChangeDto FromModel(ScimRequestsBehindChange behind) => new()
    {
        ResourceId = behind.ResourceId,
        AfterSequence = behind.AfterSequence,
        UpToSequence = behind.UpToSequence,
        Entries = behind.Entries.Select(ScimJournalEntryDto.FromEntity).ToList()
    };
}
