// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text.RegularExpressions;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Active Directory's ranged retrieval of large multi-valued attributes ([MS-ADTS] 3.1.1.3.1.3.3).
/// </summary>
/// <remarks>
/// <para>
/// A multi-valued attribute with more values than the directory's MaxValRange (1,500 by default) is not returned
/// whole. The directory answers with the first range under an attribute description carrying a range option,
/// <c>member;range=0-1499</c>, and leaves the plain <c>member</c> out of the entry altogether. The rest is read by
/// asking for the attribute from the next index, <c>member;range=1500-*</c>; the directory answers each request
/// with the actual range it returned, and a range ending in <c>*</c> is the last. Samba AD returns every value in
/// one attribute, so the integration lab never met a ranged attribute (#1853).
/// </para>
/// <para>
/// Read naively, a group over MaxValRange either imports with no members at all (nothing under <c>member</c>) or,
/// as here before this class existed, fails as a configuration error because <c>member;range=0-1499</c> is not a
/// schema attribute. Either way the group's membership silently diverges from the directory.
/// </para>
/// </remarks>
internal static partial class LdapRangedAttribute
{
    [GeneratedRegex(@"^(?<name>[^;]+);range=(?<low>\d+)-(?<high>\d+|\*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangeOption();

    /// <summary>
    /// Splits an attribute description carrying a range option into the attribute's name and the range it
    /// covers. <paramref name="high"/> is null for a range ending in <c>*</c>, which is the last.
    /// </summary>
    /// <returns>False when the description carries no range option, in which case <paramref name="attributeName"/> is the description unchanged.</returns>
    internal static bool TryParse(string attributeDescription, out string attributeName, out int low, out int? high)
    {
        attributeName = attributeDescription;
        low = 0;
        high = null;

        var match = RangeOption().Match(attributeDescription);
        if (!match.Success)
            return false;

        attributeName = match.Groups["name"].Value;
        low = int.Parse(match.Groups["low"].Value, CultureInfo.InvariantCulture);
        var highText = match.Groups["high"].Value;
        high = highText == "*" ? null : int.Parse(highText, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Reads every value of an attribute the directory answered in ranges: the values already on the entry under
    /// <paramref name="attributeDescription"/>, then each further range from the directory until it answers the
    /// last one. The result carries the attribute's plain name, so it reads like an attribute the directory returned
    /// whole.
    /// </summary>
    /// <param name="executor">Where the follow-up reads go.</param>
    /// <param name="entry">The entry as the search returned it.</param>
    /// <param name="attributeDescription">The range-qualified description the entry carries, e.g. <c>member;range=0-1499</c>.</param>
    /// <param name="timeout">How long to wait for each follow-up read.</param>
    /// <param name="logger">Where an interrupted read is reported.</param>
    internal static DirectoryAttribute ReadAll(ILdapOperationExecutor executor, SearchResultEntry entry, string attributeDescription, TimeSpan timeout, ILogger logger)
    {
        if (!TryParse(attributeDescription, out var attributeName, out _, out var high))
            throw new ArgumentException($"'{attributeDescription}' carries no range option.", nameof(attributeDescription));

        var values = new List<object>();
        AppendValues(values, entry.Attributes[attributeDescription]);
        var rangesRead = 1;

        while (high != null)
        {
            var next = high.Value + 1;
            var request = new SearchRequest(entry.DistinguishedName, "(objectClass=*)", SearchScope.Base, $"{attributeName};range={next}-*");
            var response = (SearchResponse)executor.SendRequest(request, timeout);
            if (response.Entries.Count == 0)
            {
                // The entry went away between the search that returned it and this read. What was read is kept:
                // the import will find the entry gone on its next run, and a partial value set is what the
                // directory itself would have answered at that moment.
                logger.Warning("LdapRangedAttribute: '{Dn}' was not found when reading {Attribute} from value {Next}; keeping the {Count} values already read.",
                    LogSanitiser.Sanitise(entry.DistinguishedName), attributeName, next, values.Count);
                break;
            }

            var returned = FindReturnedRange(response.Entries[0], attributeName, out var returnedHigh);
            if (returned == null)
            {
                logger.Warning("LdapRangedAttribute: The directory answered no further values of {Attribute} on '{Dn}' from value {Next}; keeping the {Count} values already read.",
                    attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), next, values.Count);
                break;
            }

            AppendValues(values, response.Entries[0].Attributes[returned]);
            rangesRead++;

            if (returnedHigh is { } answeredHigh && answeredHigh < next)
            {
                // A directory that answers a range it has already answered would loop for ever; stop and say so.
                logger.Warning("LdapRangedAttribute: The directory answered {Returned} for a request from value {Next} on '{Dn}'; stopping with the {Count} values read.",
                    returned, next, LogSanitiser.Sanitise(entry.DistinguishedName), values.Count);
                break;
            }

            high = returnedHigh;
        }

        logger.Debug("LdapRangedAttribute: Read {Count} values of {Attribute} on '{Dn}' in {Ranges} range(s).",
            values.Count, attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), rangesRead);
        return new DirectoryAttribute(attributeName, values.ToArray());
    }

    /// <summary>
    /// The attribute description the directory answered a follow-up read with: the next range, or the plain
    /// attribute if it chose to answer the whole thing. Null when it answered neither.
    /// </summary>
    private static string? FindReturnedRange(SearchResultEntry page, string attributeName, out int? high)
    {
        high = null;
        foreach (string description in page.Attributes.AttributeNames)
        {
            if (description.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                return description;

            if (TryParse(description, out var name, out _, out var rangeHigh) && name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            {
                high = rangeHigh;
                return description;
            }
        }

        return null;
    }

    private static void AppendValues(List<object> values, DirectoryAttribute attribute)
    {
        // Bytes are the lossless form: the platform stores a value as either a string or its bytes and converts on
        // the way out, so an attribute rebuilt from bytes reads back as strings or bytes exactly as the original.
        foreach (var value in attribute.GetValues(typeof(byte[])))
            values.Add(value);
    }
}
