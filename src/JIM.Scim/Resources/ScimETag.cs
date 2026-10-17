// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Scim.Resources;

/// <summary>
/// A resource's entity tag and the <c>If-Match</c> precondition (RFC 7644 section 3.14). The tag is a weak entity tag
/// over the resource's version (<c>W/"5"</c>), sent in the <c>ETag</c> header and as <c>meta.version</c>.
/// <para>
/// <c>If-Match</c> is compared weakly: SCIM's versions are weak tags, which RFC 9110's strong comparison would never
/// match, and a client sends back <c>meta.version</c> as it read it, with or without the weak prefix.
/// </para>
/// </summary>
public static class ScimETag
{
    private const string WeakPrefix = "W/";

    /// <summary>The entity tag of a resource at <paramref name="version"/>.</summary>
    public static string For(long version) => $"{WeakPrefix}\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Whether an <c>If-Match</c> header allows a write to a resource at <paramref name="version"/>: <c>*</c>, or a list
    /// of entity tags one of which is the version's. A header that is not a well-formed list matches nothing, so a
    /// malformed precondition fails safe rather than letting the write through.
    /// </summary>
    public static bool IfMatchSatisfied(string ifMatch, long version)
    {
        ArgumentNullException.ThrowIfNull(ifMatch);

        if (ifMatch.Trim() == "*")
            return true;

        var expected = version.ToString(CultureInfo.InvariantCulture);
        var position = 0;

        while (true)
        {
            while (position < ifMatch.Length && (char.IsWhiteSpace(ifMatch[position]) || ifMatch[position] == ','))
                position++;

            if (position >= ifMatch.Length)
                return false;

            if (string.Compare(ifMatch, position, WeakPrefix, 0, WeakPrefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
                position += WeakPrefix.Length;

            if (position >= ifMatch.Length || ifMatch[position] != '"')
                return false;

            var closing = ifMatch.IndexOf('"', position + 1);
            if (closing < 0)
                return false;

            if (string.Equals(ifMatch.Substring(position + 1, closing - position - 1), expected, StringComparison.Ordinal))
                return true;

            position = closing + 1;
        }
    }
}
