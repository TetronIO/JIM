// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace JIM.Connectors.LDAP;

/// <summary>
/// A Relative Distinguished Name (RDN): the leftmost component of a Distinguished Name, for example
/// "CN=John Smith" in "CN=John Smith,OU=Users,DC=example,DC=com". Most RDNs have a single attribute
/// type/value pair, but a multi-valued RDN joins several with '+' (for example "CN=John+SN=Smith").
/// </summary>
internal sealed class LdapRelativeDistinguishedName
{
    /// <summary>
    /// The verbatim source text of this RDN exactly as it appeared in the Distinguished Name
    /// (for example "CN=Smith\, John"). Preserving the original text lets a reconstructed parent
    /// DN match the directory's own formatting rather than a re-normalised form.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// The attribute type/value components of this RDN. Single-valued RDNs have exactly one;
    /// multi-valued RDNs (joined by '+') have more than one.
    /// </summary>
    public IReadOnlyList<LdapAttributeTypeValue> Components { get; }

    private LdapRelativeDistinguishedName(string source, IReadOnlyList<LdapAttributeTypeValue> components)
    {
        Source = source;
        Components = components;
    }

    /// <summary>
    /// Attempts to parse a single RDN. Returns false when the source is empty or any component
    /// lacks an attribute type (has no unescaped '=', or an empty type before it).
    /// </summary>
    public static bool TryParse(string source, [NotNullWhen(true)] out LdapRelativeDistinguishedName? result)
    {
        result = null;
        if (string.IsNullOrEmpty(source))
            return false;

        var components = new List<LdapAttributeTypeValue>();
        foreach (var componentSource in LdapDistinguishedName.SplitTopLevel(source, '+'))
        {
            var equalsIndex = FindFirstUnescapedEquals(componentSource);
            if (equalsIndex <= 0)
                return false;

            var type = componentSource[..equalsIndex].Trim();
            if (type.Length == 0)
                return false;

            var rawValue = componentSource[(equalsIndex + 1)..];
            components.Add(new LdapAttributeTypeValue(type, Unescape(rawValue)));
        }

        if (components.Count == 0)
            return false;

        result = new LdapRelativeDistinguishedName(source, components);
        return true;
    }

    public override string ToString() => Source;

    /// <summary>
    /// Finds the index of the first '=' that separates the attribute type from its value, skipping any
    /// '=' that is escaped (preceded by an odd number of backslashes) or enclosed in RFC 2253 quotes.
    /// </summary>
    private static int FindFirstUnescapedEquals(string source)
    {
        var backslashes = 0;
        var inQuotes = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"' && backslashes % 2 == 0)
                inQuotes = !inQuotes;
            else if (c == '=' && backslashes % 2 == 0 && !inQuotes)
                return i;

            backslashes = 0;
        }

        return -1;
    }

    /// <summary>
    /// Resolves RFC 4514 escaping in an attribute value: strips RFC 2253 surrounding quotes, turns
    /// "\c" single-character escapes into the literal character, and "\XX" hex-pair escapes into the
    /// character with that byte value.
    /// </summary>
    private static string Unescape(string raw)
    {
        // RFC 2253 quoted value: the surrounding quotes are delimiters, not part of the value.
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];

        if (!raw.Contains('\\'))
            return raw;

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                if (i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
                {
                    builder.Append((char)((HexValue(raw[i + 1]) << 4) + HexValue(raw[i + 2])));
                    i += 2;
                }
                else
                {
                    builder.Append(raw[i + 1]);
                    i += 1;
                }
            }
            else
            {
                builder.Append(raw[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int HexValue(char c) => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;
}
