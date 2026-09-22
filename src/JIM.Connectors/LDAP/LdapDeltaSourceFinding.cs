// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What a change source established about its own readiness: whether the account JIM connects as can read the
/// thing a Delta Import reads changes from. The same three-way answer <c>LdapConnectorDeletedObjectsAccess</c>
/// gives for one partition's Deleted Objects container, made general so that every source is judged alike.
/// <para><b>Every silence is an unknown, never a denial.</b> A source may only report
/// <see cref="Unavailable"/> from evidence it read in full.</para>
/// </summary>
internal enum LdapDeltaSourceOutcome
{
    /// <summary>The source can be read; a Delta Import will see every change.</summary>
    Available,

    /// <summary>It provably cannot; a Delta Import would import changes and miss others, so it refuses to run.</summary>
    Unavailable,

    /// <summary>JIM could not see enough to say; the Delta Import runs and carries a warning.</summary>
    CouldNotDetermine
}

/// <summary>
/// One thing a change source checked, and what it found. A source may report several (the USN source reports one
/// per partition); each carries the text for both moments it is shown, so that the shell and Schema Discovery
/// need know nothing about the source to present it.
/// </summary>
internal sealed class LdapDeltaSourceFinding
{
    /// <summary>What was checked: a Deleted Objects container, cn=accesslog, or the changelog's DN.</summary>
    internal required string Subject { get; init; }

    internal required LdapDeltaSourceOutcome Outcome { get; init; }

    /// <summary>A plain statement of what was found, and where the outcome is not a grant, why.</summary>
    internal required string Detail { get; init; }

    /// <summary>
    /// What a Delta Import reports: the failure that stops it when the source is unavailable, the warning it
    /// carries when JIM could not be sure. Null when available, which has nothing to say.
    /// </summary>
    internal string? DeltaImportText { get; init; }

    /// <summary>
    /// What Schema Discovery warns, so an administrator learns of the problem while setting the Connected System
    /// up rather than from an import that found nothing. Null when available.
    /// </summary>
    internal string? SchemaDiscoveryText { get; init; }
}
