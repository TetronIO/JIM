// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Scim;

/// <summary>
/// One accepted SCIM write to one resource, recorded in the same transaction as the write itself: what changed, which
/// attributes, by which API key and from which address.
/// </summary>
/// <remarks>
/// <para>
/// The journal is the audit trail between imports. An import sees each resource only as it stands when the import
/// runs, so without the journal a resource created and deleted between two imports would leave no trace in JIM, an
/// attribute changed twice would show only its last value, and no record would say which key made a change.
/// </para>
/// <para>
/// It names attributes, never values: the values are in the store and, once imported, in the Connected System
/// Object's change history, and a second copy of personal data here would buy nothing. Entries are removed by the
/// history retention cleanup. They outlive the resource they describe (a tombstone is purged long before), so
/// <see cref="ResourceId"/> carries no foreign key.
/// </para>
/// </remarks>
public class ScimStoreJournalEntry
{
    public long Id { get; set; }

    /// <summary>
    /// The Connected System whose store was written.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The write's change sequence; every entry written by one request shares it.
    /// </summary>
    public long ChangeSequence { get; set; }

    /// <summary>
    /// The SCIM <c>id</c> of the resource written.
    /// </summary>
    public Guid ResourceId { get; set; }

    /// <summary>
    /// The resource type's name when the write was made (for example <c>User</c>): a name rather than a key, so the
    /// entry still reads correctly after the type's definition is removed.
    /// </summary>
    [MaxLength(ScimStoreLimits.NameMaxLength)]
    public string ResourceTypeName { get; set; } = null!;

    public ScimStoreOperation Operation { get; set; }

    /// <summary>
    /// The attributes the write changed, by SCIM name (<c>name.givenName</c>, an extension's URN-qualified name).
    /// Empty for a deletion.
    /// </summary>
    public List<string> ChangedAttributes { get; set; } = [];

    /// <summary>
    /// The attributes the request carried that the store's definitions do not declare, and which the store therefore
    /// did not keep (a custom extension mapped at the identity provider, for example), by SCIM name. Names only, as for
    /// <see cref="ChangedAttributes"/>; read-only attributes are not listed.
    /// </summary>
    public List<string> IgnoredAttributes { get; set; } = [];

    /// <summary>
    /// The API key that made the request; null once that key is deleted (see <see cref="ApiKeyName"/>).
    /// </summary>
    public Guid? ApiKeyId { get; set; }

    /// <summary>
    /// The API key's name when the request was made, so the entry still says who made it after the key is deleted.
    /// </summary>
    public string? ApiKeyName { get; set; }

    /// <summary>
    /// The address the request came from, as JIM saw it (after any trusted proxy).
    /// </summary>
    [MaxLength(64)]
    public string? ClientAddress { get; set; }

    /// <summary>
    /// When the write happened (UTC): the same instant as the resource's <c>meta.lastModified</c>.
    /// </summary>
    public DateTime Occurred { get; set; }
}
