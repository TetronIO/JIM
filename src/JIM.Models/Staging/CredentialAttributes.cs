// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.ObjectModel;

namespace JIM.Models.Staging;

/// <summary>
/// The denylist of Connected System attributes that hold credential material, and the heuristic that spots
/// attributes which merely look like they might.
/// <para>
/// Denied attributes are never imported, never stored as managed, and can never be the source or the target of an
/// Attribute Flow. Two independent reasons make this non-negotiable:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Most of them cannot be meaningfully read back. A directory returns nothing at all for <c>unicodePwd</c>, and
/// returns opaque hash blobs for the history attributes, so anything JIM imported would be either empty or
/// meaningless, and every subsequent synchronisation would see a spurious change.
/// </description></item>
/// <item><description>
/// The rest hold live credential material. A password (or a hash of one) that lands in the Metaverse is
/// replicated to every other Connected System in scope, written into change history, and rendered in the portal.
/// Credential material must never enter the Metaverse under any circumstances.
/// </description></item>
/// </list>
/// <para>
/// Passwords are not synchronised through Attribute Flow at all. JIM uses a dedicated, write-only password
/// channel instead (<c>IConnectorPasswordManagement</c>, in <c>src/JIM.Models/Interfaces/</c>): a password is
/// pushed to a Connected System and never read back, so it is never held in the Metaverse. The LDAP Connector
/// owns writing <c>unicodePwd</c> itself, with the correct encoding (a UTF-16LE, quote-wrapped value) and only
/// over LDAPS, which is why exposing the attribute to Attribute Flow would be both unsafe and redundant.
/// </para>
/// </summary>
public static class CredentialAttributes
{
    /// <summary>
    /// The denied attribute names. Matching is ordinal, ignoring case, because Connected Systems differ in the
    /// casing they report for the same attribute.
    /// </summary>
    private static readonly ReadOnlyCollection<string> DeniedNames = new(
    [
        "unicodePwd",
        "userPassword",
        "dBCSPwd",
        "ntPwdHistory",
        "lmPwdHistory",
        "supplementalCredentials",
        "unixUserPassword",
        "msDS-ManagedPassword"
    ]);

    private static readonly HashSet<string> DeniedNameLookup = new(DeniedNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Substrings that suggest an attribute may carry credential material. Deliberately broad: this drives a
    /// warning only, never a block.
    /// </summary>
    private static readonly string[] CredentialLikeFragments =
    [
        "password",
        "passwd",
        "pwd",
        "credential",
        "secret"
    ];

    /// <summary>
    /// The denied attribute names, for display in the portal and in documentation.
    /// </summary>
    public static IReadOnlyCollection<string> All => DeniedNames;

    /// <summary>
    /// Whether the supplied attribute name is on the credential denylist and must therefore be blocked: never
    /// imported, never selectable, and never usable in an Attribute Flow.
    /// </summary>
    /// <param name="attributeName">The Connected System attribute name to test. Null, empty and whitespace-only names are not credential attributes.</param>
    /// <returns>True when the name matches a denied name, compared ordinally and ignoring case.</returns>
    public static bool IsCredentialAttribute(string? attributeName)
    {
        return !string.IsNullOrWhiteSpace(attributeName) && DeniedNameLookup.Contains(attributeName);
    }

    /// <summary>
    /// Whether the supplied attribute name looks like it may carry credential material without being on the
    /// denylist proper. This is the "warn but do not block" case: it flags attributes JIM cannot know about (a
    /// bespoke <c>customPasswordField</c> in a line-of-business directory, for example) so an administrator can
    /// make an informed decision.
    /// </summary>
    /// <remarks>
    /// This is a substring heuristic and it produces false positives by design. Ordinary, entirely safe directory
    /// attributes such as <c>pwdLastSet</c>, <c>badPwdCount</c>, <c>pwdProperties</c> and
    /// <c>passwordHistoryLength</c> all match it, and administrators routinely and legitimately import them. It
    /// must therefore only ever drive a warning; anything that blocks belongs on the denylist instead. Names that
    /// are already on the denylist return false here, because they are blocked rather than warned about.
    /// </remarks>
    /// <param name="attributeName">The Connected System attribute name to test. Null, empty and whitespace-only names never match.</param>
    /// <returns>True when the name is not denied but contains a credential-suggesting fragment, compared ordinally and ignoring case.</returns>
    public static bool HasCredentialLikeName(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
            return false;

        // Denied names are blocked, not warned about.
        if (DeniedNameLookup.Contains(attributeName))
            return false;

        return CredentialLikeFragments.Any(fragment => attributeName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
