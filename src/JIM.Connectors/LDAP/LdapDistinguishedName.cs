// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;

namespace JIM.Connectors.LDAP;

/// <summary>
/// A minimal, RFC 4514 / RFC 2253 aware parser for LDAP Distinguished Names, covering the operations the
/// LDAP connector needs: splitting a DN into its RDNs, reading each component's attribute type and value,
/// and deriving the parent DN. It is deliberately scoped to JIM's usage rather than being a general-purpose
/// DN library.
///
/// Escaping is honoured so that separators inside values do not split incorrectly: a comma, plus, or equals
/// is treated as a delimiter only when it is not escaped (preceded by an odd number of backslashes) and not
/// enclosed in RFC 2253 double quotes.
/// </summary>
internal sealed class LdapDistinguishedName
{
    /// <summary>
    /// The Relative Distinguished Names that make up this DN, ordered leaf-first (most specific first).
    /// </summary>
    public IReadOnlyList<LdapRelativeDistinguishedName> Rdns { get; }

    private LdapDistinguishedName(IReadOnlyList<LdapRelativeDistinguishedName> rdns)
    {
        Rdns = rdns;
    }

    /// <summary>
    /// The leaf (most specific) RDN; for example "CN=John Smith" in "CN=John Smith,OU=Users,DC=example,DC=com".
    /// </summary>
    public LdapRelativeDistinguishedName LeafRdn => Rdns[0];

    /// <summary>
    /// The parent DN (this DN with its leaf RDN removed), or null when this DN has only a single RDN.
    /// The result preserves the verbatim text of each remaining RDN, so <see cref="ToString"/> reproduces
    /// the directory's own formatting rather than a re-normalised form.
    /// </summary>
    public LdapDistinguishedName? Parent => Rdns.Count > 1
        ? new LdapDistinguishedName(Rdns.Skip(1).ToList())
        : null;

    /// <summary>
    /// Parses a Distinguished Name. Throws <see cref="FormatException"/> when the input is null, empty, or
    /// not a well-formed DN. The message deliberately omits the DN value, which may contain personal data.
    /// </summary>
    public static LdapDistinguishedName Parse(string dn)
    {
        if (!TryParse(dn, out var result))
            throw new FormatException("The value is not a valid Distinguished Name.");

        return result;
    }

    /// <summary>
    /// Attempts to parse a Distinguished Name. Returns false (with a null result) when the input is null,
    /// empty, or malformed.
    /// </summary>
    public static bool TryParse(string? dn, [NotNullWhen(true)] out LdapDistinguishedName? result)
    {
        result = null;
        if (string.IsNullOrEmpty(dn))
            return false;

        var rdns = new List<LdapRelativeDistinguishedName>();
        foreach (var rdnSource in SplitTopLevel(dn, ','))
        {
            if (!LdapRelativeDistinguishedName.TryParse(rdnSource, out var rdn))
                return false;

            rdns.Add(rdn);
        }

        if (rdns.Count == 0)
            return false;

        result = new LdapDistinguishedName(rdns);
        return true;
    }

    public override string ToString() => string.Join(",", Rdns.Select(r => r.Source));

    /// <summary>
    /// Splits a DN or RDN string on the given separator, ignoring separators that are escaped (preceded by an
    /// odd number of backslashes) or enclosed in RFC 2253 double quotes. Returns the verbatim substrings, so
    /// joining them back with the separator reproduces the original text.
    /// </summary>
    internal static List<string> SplitTopLevel(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var backslashes = 0;
        var inQuotes = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"' && backslashes % 2 == 0)
                inQuotes = !inQuotes;
            else if (c == separator && backslashes % 2 == 0 && !inQuotes)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }

            backslashes = 0;
        }

        parts.Add(value[start..]);
        return parts;
    }
}
