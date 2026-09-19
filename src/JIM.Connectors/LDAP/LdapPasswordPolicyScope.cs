// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The facts from a directory's rootDSE that a password policy reader needs in order to know where a policy could
/// live and whether one is advertised at all. Built once from the rootDSE so that the readers, the preflight and
/// the schema import all ask the same questions of the same answers.
/// </summary>
/// <param name="DefaultNamingContext">
/// The directory's defaultNamingContext, which Active Directory publishes as the domain root. Null where the
/// directory does not publish one.
/// </param>
/// <param name="NamingContexts">
/// The naming contexts that could hold user entries, in the order the directory listed them, with the
/// configuration, schema, monitoring and log contexts removed. Empty when the rootDSE did not say.
/// </param>
/// <param name="ConfigContext">
/// The DN of the server configuration ("cn=config" on OpenLDAP and 389 Directory Server), where those directories
/// hold their password policy configuration. Null where not published.
/// </param>
/// <param name="AdvertisesPasswordPolicyControl">
/// Whether the directory advertises the password policy request control, which OpenLDAP does only when the ppolicy
/// overlay is loaded.
/// </param>
internal sealed record LdapPasswordPolicyScope(
    string? DefaultNamingContext,
    IReadOnlyList<string> NamingContexts,
    string? ConfigContext,
    bool AdvertisesPasswordPolicyControl)
{
    /// <summary>
    /// The password policy request control (draft-behera-ldap-password-policy), advertised in supportedControl
    /// by a directory with a password policy mechanism loaded.
    /// </summary>
    internal const string PasswordPolicyRequestControlOid = "1.3.6.1.4.1.42.2.27.8.5.1";

    /// <summary>
    /// The naming context a directory-wide policy is read from: the default naming context where the directory
    /// publishes one, otherwise the first context that could hold users. Null when the directory told JIM nothing
    /// about where its data lives.
    /// </summary>
    internal string? PrimaryNamingContext => DefaultNamingContext ?? NamingContexts.FirstOrDefault();

    /// <summary>
    /// Derives the scope from what the rootDSE published.
    /// </summary>
    internal static LdapPasswordPolicyScope From(LdapConnectorRootDse rootDse)
    {
        var userContexts = (rootDse.NamingContexts ?? [])
            .Where(context => CouldHoldUsers(context, rootDse.ConfigContext))
            .ToList();

        var advertisesControl = rootDse.SupportedControls?.Contains(PasswordPolicyRequestControlOid, StringComparer.Ordinal) == true;

        return new LdapPasswordPolicyScope(rootDse.DefaultNamingContext, userContexts, rootDse.ConfigContext, advertisesControl);
    }

    /// <summary>
    /// Whether a naming context is somewhere user entries could live. The configuration, schema, monitoring and
    /// change log contexts are all named by a cn, and none of them holds accounts; user data lives under dc or o
    /// suffixes. A probe spent on cn=config is a search spent on nothing, and on a locked-down directory it is a
    /// refusal that would read as "could not determine" for no reason.
    /// </summary>
    private static bool CouldHoldUsers(string namingContext, string? configContext)
    {
        var trimmed = namingContext.Trim();
        if (trimmed.Length == 0)
            return false;

        if (configContext != null && string.Equals(trimmed, configContext.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (trimmed.StartsWith("cn=", StringComparison.OrdinalIgnoreCase))
            return false;

        // The legacy administration suffix on Netscape-derived servers, which holds server configuration.
        return !string.Equals(trimmed, "o=netscaperoot", StringComparison.OrdinalIgnoreCase);
    }
}
