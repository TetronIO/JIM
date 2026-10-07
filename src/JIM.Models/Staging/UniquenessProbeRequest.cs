// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One batch of a live uniqueness probe (Unique Value Generation, #242, release 3): one object's candidates for one
/// attribute of one Connected System, plus a control value when JIM holds one.
/// </summary>
public sealed class UniquenessProbeRequest
{
    /// <summary>
    /// The most candidates one batch may carry: an object's lazy window (plan decision 16).
    /// </summary>
    public const int MaximumCandidates = 10;

    /// <summary>
    /// The name of the Connected System Object Type the attribute belongs to. A SCIM service provider is searched at
    /// that resource type's endpoint and a database in that Object Type's table or view (#1941); an LDAP directory is
    /// searched by attribute alone and does not read it.
    /// </summary>
    public required string ObjectTypeName { get; init; }

    /// <summary>
    /// The attribute's name in the Connected System (for an LDAP directory, the LDAP attribute name).
    /// </summary>
    public required string AttributeName { get; init; }

    /// <summary>
    /// The values to look for, at least one and at most <see cref="MaximumCandidates"/>. The result carries one
    /// outcome per candidate, in this order.
    /// </summary>
    public required IReadOnlyList<string> Candidates { get; init; }

    /// <summary>
    /// A value JIM already holds in this Connected System's Connector Space for the attribute, never one of the
    /// <see cref="Candidates"/>. Rides in the same search so its absence from the answer exposes a search that
    /// cannot see what it is looking for. Null when JIM holds no such value (a first load, typically): the search
    /// then still runs, and a candidate it returns is in use whatever the bind can see, but a candidate it does not
    /// return is unconfirmed (plan decision 14, revised 2026-10-06).
    /// </summary>
    public string? ControlValue { get; init; }

    /// <summary>
    /// How long the Connector may spend on the search before giving up.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless the batch is well formed: an object type and attribute name, one to
    /// <see cref="MaximumCandidates"/> candidates, and no control value that is itself a candidate (compared
    /// case-insensitively, as the result is).
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ObjectTypeName))
            throw new ArgumentException("A uniqueness probe needs the object type the attribute belongs to.", nameof(ObjectTypeName));

        if (string.IsNullOrWhiteSpace(AttributeName))
            throw new ArgumentException("A uniqueness probe needs the attribute to search.", nameof(AttributeName));

        if (Candidates.Count == 0 || Candidates.Count > MaximumCandidates)
            throw new ArgumentException($"A uniqueness probe batch carries between 1 and {MaximumCandidates} candidates; this one carries {Candidates.Count}.", nameof(Candidates));

        if (ControlValue != null && Candidates.Contains(ControlValue, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("A uniqueness probe's control value must not be one of its candidates: finding it would prove nothing.", nameof(ControlValue));
    }
}
