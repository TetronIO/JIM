// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What an import should persist as its pinned domain controller (issue #230 Phase 2), and anything an
/// administrator needs told about that decision.
/// </summary>
/// <param name="PinnedServer">
/// The value to persist as <see cref="LdapConnectorRootDse.PinnedDirectoryServer"/>; null leaves the
/// Connected System unpinned, so the next connection resolves via Host again.
/// </param>
/// <param name="WarningMessage">
/// Set only where JIM declined to pin a domain controller it had discovered, which is a configuration
/// problem outside JIM (the directory advertises a name the JIM host cannot resolve) that would otherwise
/// be invisible: nothing fails, delta imports simply never get the consistency a pin buys them. Surfaced on
/// the Activity; null on every ordinary outcome, including "this directory does not pin at all".
/// </param>
internal readonly record struct PinnedDirectoryServerDecision(string? PinnedServer, string? WarningMessage);
