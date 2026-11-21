// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// The Microsoft Graph application permissions the Connector needs, least privilege per capability, and the check
/// that tells an administrator which are missing.
/// </summary>
/// <remarks>
/// A broader permission satisfies a narrower one: ReadWrite covers Read, and Directory.ReadWrite.All covers users and
/// groups alike, but nothing covers Member.Read.Hidden. Reading is required; writing, Source of Authority and hidden
/// membership are needed only by the features that use them, which a Connected System may not, so their absence is a
/// warning rather than an error.
/// </remarks>
internal static class GraphPermissions
{
    public const string UserRead = "User.Read.All";
    public const string UserReadWrite = "User.ReadWrite.All";
    public const string GroupRead = "Group.Read.All";
    public const string GroupReadWrite = "Group.ReadWrite.All";
    public const string DirectoryRead = "Directory.Read.All";
    public const string DirectoryReadWrite = "Directory.ReadWrite.All";
    public const string UserSyncBehaviourRead = "User-OnPremisesSyncBehavior.Read.All";
    public const string UserSyncBehaviourReadWrite = "User-OnPremisesSyncBehavior.ReadWrite.All";
    public const string GroupSyncBehaviourRead = "Group-OnPremisesSyncBehavior.Read.All";
    public const string GroupSyncBehaviourReadWrite = "Group-OnPremisesSyncBehavior.ReadWrite.All";

    /// <summary>
    /// Reads the members of a group whose membership is hidden. No other permission does, Directory.ReadWrite.All
    /// included.
    /// </summary>
    public const string MemberReadHidden = "Member.Read.Hidden";

    /// <summary>
    /// Sets passwords, with <see cref="PasswordAdministratorRoles"/>: Graph's app-only password reset needs both. Only for
    /// a Connected System that delivers passwords.
    /// </summary>
    public const string UserPasswordProfileReadWrite = "User-PasswordProfile.ReadWrite.All";

    /// <summary>
    /// Directory roles, by template id, that let an app reset passwords, the least privileged first: User Administrator,
    /// Privileged Authentication Administrator and Global Administrator.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PasswordAdministratorRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Privileged Authentication Administrator",
        ["62e90394-69f5-4237-9190-012177145e10"] = "Global Administrator"
    };

    /// <summary>Whether the token carries a permission that lets it write a user's password profile.</summary>
    public static bool CanWritePasswordProfiles(IReadOnlyCollection<string> roles) =>
        roles.Any(r => r.Equals(UserPasswordProfileReadWrite, StringComparison.OrdinalIgnoreCase)
                       || r.Equals(UserReadWrite, StringComparison.OrdinalIgnoreCase)
                       || r.Equals(DirectoryReadWrite, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the token may read the members of a group whose membership is hidden. Graph's documentation does not say
    /// what it answers without the permission; an empty list would read as a group with no members, so the Connector
    /// does not ask unless this holds.
    /// </summary>
    public static bool CanReadHiddenMembership(IReadOnlyCollection<string> roles) =>
        roles.Contains(MemberReadHidden, StringComparer.OrdinalIgnoreCase);

    public static GraphPermissionCheckResult Check(IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0)
        {
            return new GraphPermissionCheckResult(
                "The access token carries no Microsoft Graph application permissions. On the app registration's API permissions page, " +
                $"add Microsoft Graph application permissions (not delegated ones), at least {UserRead} and {GroupRead}, then grant admin consent.",
                null, false, false);
        }

        var granted = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        bool Any(params string[] permissions) => permissions.Any(granted.Contains);

        var canReadUsers = Any(UserRead, UserReadWrite, DirectoryRead, DirectoryReadWrite);
        var canReadGroups = Any(GroupRead, GroupReadWrite, DirectoryRead, DirectoryReadWrite);
        if (!canReadUsers && !canReadGroups)
        {
            return new GraphPermissionCheckResult(
                $"The app registration cannot read users or groups. Grant the Microsoft Graph application permissions {UserRead} and {GroupRead} " +
                "(or their ReadWrite equivalents, to export too), then grant admin consent.",
                null, false, false);
        }

        var missing = new List<string>();
        if (!canReadUsers)
            missing.Add($"{UserRead} (to import users)");
        if (!canReadGroups)
            missing.Add($"{GroupRead} (to import groups)");
        if (canReadUsers && !Any(UserReadWrite, DirectoryReadWrite))
            missing.Add($"{UserReadWrite} (to export users)");
        if (canReadGroups && !Any(GroupReadWrite, DirectoryReadWrite))
            missing.Add($"{GroupReadWrite} (to export groups)");
        if (canReadUsers && !Any(UserSyncBehaviourRead, UserSyncBehaviourReadWrite))
            missing.Add($"{UserSyncBehaviourRead} (to tell which synchronised users have moved to cloud management)");
        if (canReadGroups && !Any(GroupSyncBehaviourRead, GroupSyncBehaviourReadWrite))
            missing.Add($"{GroupSyncBehaviourRead} (to tell which synchronised groups have moved to cloud management)");
        if (canReadGroups && !Any(MemberReadHidden))
            missing.Add($"{MemberReadHidden} (to import the members of groups whose membership is hidden)");

        var warning = missing.Count == 0
            ? null
            : "The app registration is missing Microsoft Graph application permissions that some features need: " +
              string.Join("; ", missing) + ". Grant them with admin consent, or ignore this if this Connected System does not use those features.";

        return new GraphPermissionCheckResult(null, warning, canReadUsers, canReadGroups);
    }
}
