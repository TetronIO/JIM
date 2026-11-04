// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// Works out the LDAP Connector's Kerberos sign-in to Exchange from the settings it already has (#2021, D8): nothing
/// about Kerberos is entered. The account is the directory account, found through the directory whichever form its
/// Username takes; the principal is its sAMAccountName at the realm its entry sits in; and the KDC is the domain
/// controller the directory bind reached, which every domain controller is.
/// </summary>
/// <remarks>
/// Using the sAMAccountName rather than the Username as written sidesteps Kerberos enterprise names: a UPN whose
/// suffix is not the realm (<c>svc-jim@panoply.com</c> in <c>PANOPLY.LOCAL</c>) is not a principal the KDC knows by
/// that name. An account outside the connected domain is refused: a stated limit of the first version.
/// </remarks>
internal static class LdapExchangeSignIn
{
    private const string EndpointPath = "/PowerShell/";

    /// <summary>
    /// How to find the account a Username names: a distinguished name, <c>DOMAIN\name</c>, a UPN, or a bare name.
    /// </summary>
    internal static LdapExchangeAccountLookup ParseUsername(string username)
    {
        var value = username.Trim();

        // A DN first: it can carry backslash escapes and an @ in a value, so it must not be read as either other form.
        if (value.Contains('=') && LdapDistinguishedName.TryParse(value, out _))
            return new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.DistinguishedName, value, null);

        var separator = value.IndexOf('\\');
        if (separator > 0)
            return new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.SamAccountName, value[(separator + 1)..], value[..separator]);

        return value.Contains('@')
            ? new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.UserPrincipalName, value, null)
            : new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.SamAccountName, value, null);
    }

    /// <summary>
    /// The search that finds the account: the whole domain by sAMAccountName or userPrincipalName, the value escaped so
    /// a Username can never widen it, or a read of the one entry a distinguished name names.
    /// </summary>
    internal static LdapExchangeAccountSearch SearchFor(LdapExchangeAccountLookup lookup, string domainNamingContext) => lookup.Kind switch
    {
        LdapExchangeAccountLookupKind.DistinguishedName => new LdapExchangeAccountSearch(lookup.Value, "(objectClass=*)", WholeSubtree: false),
        LdapExchangeAccountLookupKind.UserPrincipalName => new LdapExchangeAccountSearch(domainNamingContext,
            $"(&(objectCategory=person)(userPrincipalName={LdapConnectorUtilities.EscapeLdapFilterValue(lookup.Value)}))", WholeSubtree: true),
        _ => new LdapExchangeAccountSearch(domainNamingContext,
            $"(&(objectCategory=person)(sAMAccountName={LdapConnectorUtilities.EscapeLdapFilterValue(lookup.Value)}))", WholeSubtree: true)
    };

    /// <summary>
    /// The Kerberos realm a distinguished name sits in: its <c>DC=</c> components, in capitals and joined with dots. Null
    /// when it has none or is not a distinguished name.
    /// </summary>
    internal static string? RealmFrom(string distinguishedName)
    {
        if (!LdapDistinguishedName.TryParse(distinguishedName, out var parsed))
            return null;

        var domainComponents = parsed.Rdns
            .SelectMany(rdn => rdn.Components)
            .Where(component => string.Equals(component.Type, "DC", StringComparison.OrdinalIgnoreCase))
            .Select(component => component.Value.ToUpperInvariant())
            .ToList();

        return domainComponents.Count == 0 ? null : string.Join('.', domainComponents);
    }

    /// <summary>
    /// The sign-in, or why there cannot be one.
    /// </summary>
    /// <param name="lookup">How the Username named the account.</param>
    /// <param name="account">The account the search found, or null when it found none.</param>
    /// <param name="domain">The domain the Connected System is connected to.</param>
    internal static LdapExchangeSignInResolution Resolve(LdapExchangeAccountLookup lookup, LdapExchangeAccount? account, LdapExchangeDomain domain)
    {
        var domainRealm = RealmFrom(domain.NamingContext);
        var domainName = domainRealm?.ToLowerInvariant() ?? domain.NamingContext;

        if (lookup.NetBiosDomain != null && domain.NetBiosName != null &&
            !string.Equals(lookup.NetBiosDomain, domain.NetBiosName, StringComparison.OrdinalIgnoreCase))
        {
            return Refused($"The Username names the {lookup.NetBiosDomain} domain, but this Connected System is connected to {domain.NetBiosName} ({domainName}). " +
                           "JIM signs in to Exchange as the directory account, so it must be an account in the connected domain.");
        }

        if (account == null)
        {
            return Refused($"JIM could not find the account {lookup.Value} in {domainName}, so it cannot sign in to Exchange as it. " +
                           "JIM signs in to Exchange as the directory account, so it must be an account in the connected domain.");
        }

        var accountRealm = RealmFrom(account.DistinguishedName);
        if (accountRealm == null || !string.Equals(accountRealm, domainRealm, StringComparison.OrdinalIgnoreCase))
        {
            return Refused($"The account {account.DistinguishedName} is not in {domainName}, the domain this Connected System is connected to. " +
                           "JIM signs in to Exchange as the directory account, so it must be an account in the connected domain.");
        }

        if (string.IsNullOrWhiteSpace(domain.KdcHost))
        {
            return Refused("The domain controller JIM connected to did not give its DNS name, so JIM cannot ask it for the Kerberos ticket Exchange needs. " +
                           "Set the Preferred Domain Controller to the domain controller's DNS name.");
        }

        return new LdapExchangeSignInResolution(new LdapExchangeKerberosSignIn($"{account.SamAccountName}@{accountRealm}", accountRealm, domain.KdcHost), null);
    }

    /// <summary>
    /// The remote PowerShell endpoint an Exchange server setting names: a host name (with a port, if it has one) is
    /// Exchange's own endpoint on it over http, where Kerberos encrypts the session; a full http:// or https:// address
    /// is used as it is.
    /// </summary>
    internal static LdapExchangeEndpoint ParseExchangeServer(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Unusable();

        var hasScheme = trimmed.Contains("://", StringComparison.Ordinal);
        var candidate = hasScheme ? trimmed : $"http://{trimmed}{EndpointPath}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(endpoint.Host))
            return Unusable();

        return new LdapExchangeEndpoint(endpoint, null);
    }

    private static LdapExchangeSignInResolution Refused(string refusal) => new(null, refusal);

    private static LdapExchangeEndpoint Unusable() => new(null,
        "Enter the Exchange server's host name, such as ex1.panoply.local, or its remote PowerShell address, such as https://ex1.panoply.local/PowerShell/.");
}
