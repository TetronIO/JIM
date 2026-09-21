// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;
namespace JIM.Connectors.LDAP;

/// <summary>
/// The one place a change source is chosen. Everything that needs one (the import, Schema Discovery) asks here
/// with the kind the rootDSE record derives from the directory type, so no caller switches on directory type
/// to decide how changes are read.
/// </summary>
internal static class LdapDeltaSources
{
    internal static ILdapDeltaSource Create(LdapDeltaSourceKind kind, ILdapOperationExecutor executor, ILogger logger) => kind switch
    {
        LdapDeltaSourceKind.Usn => new LdapUsnDeltaSource(executor, logger),
        LdapDeltaSourceKind.Accesslog => new LdapAccesslogDeltaSource(executor, logger),
        LdapDeltaSourceKind.Changelog => new LdapChangelogDeltaSource(executor, logger),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No change source reads this kind of directory.")
    };
}
