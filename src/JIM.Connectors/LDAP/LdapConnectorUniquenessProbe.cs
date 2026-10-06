// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Text;
using System.Text.RegularExpressions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Searches a directory for generated values already in use (Unique Value Generation, #242, release 3, plan Phase 7
/// item 3). One search per partition root, from the root down, with one OR filter carrying the object's candidates
/// and the control value, asking for the probed attribute and nothing else.
/// <para>
/// The partition root, never the selected containers: a value held by an account outside JIM's import scope is
/// exactly the collision the probe exists to catch, since JIM already knows about everything inside it. Forest-wide
/// attributes searched through a Global Catalog are tracked by #1940.
/// </para>
/// </summary>
internal sealed partial class LdapConnectorUniquenessProbe
{
    /// <summary>
    /// The most entries one search may return. A probed attribute is meant to be unique, so a batch of at most ten
    /// candidates and a control should match a handful of entries; anything near this means the attribute is not
    /// unique in the directory, and the answer would be cut short rather than trusted.
    /// </summary>
    internal const int SizeLimit = 100;

    /// <summary>
    /// Naming attributes whose values are unique only within their container, plus the Distinguished Name itself.
    /// Two "John Smith" entries in different OUs are not a collision, so a partition-wide search on these would
    /// reject values that are free and add needless suffixes (plan Phase 7 item 3). <c>uid</c> is deliberately not
    /// here: where it names entries it is still conventionally unique across the directory, and it is the attribute
    /// a brownfield OpenLDAP estate most needs probed.
    /// </summary>
    private static readonly HashSet<string> ContainerScopedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "distinguishedName", "dn", "entryDN", "cn", "ou", "o", "dc", "c", "l", "st"
    };

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorUniquenessProbe(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Whether a partition-wide search for <paramref name="attributeName"/> means anything (see
    /// <see cref="ContainerScopedAttributes"/>).
    /// </summary>
    internal static bool CanProbeAttribute(string attributeName) =>
        !string.IsNullOrWhiteSpace(attributeName) && !ContainerScopedAttributes.Contains(attributeName);

    /// <summary>
    /// Whether <paramref name="attributeName"/> is a plain RFC 4512 descriptor (a letter, then letters, digits and
    /// hyphens) or a numeric OID. The attribute name is not an assertion value, so RFC 4515 escaping does not apply
    /// to it; it must instead be proven harmless before it is put into a filter.
    /// </summary>
    internal static bool IsValidAttributeDescription(string attributeName) =>
        !string.IsNullOrEmpty(attributeName) && AttributeDescriptionPattern().IsMatch(attributeName);

    /// <summary>
    /// The OR filter for one batch: an equality assertion per value, every value escaped per RFC 4515.
    /// </summary>
    internal static string BuildFilter(string attributeName, IEnumerable<string> values)
    {
        var filter = new StringBuilder("(|");
        foreach (var value in values)
            filter.Append('(').Append(attributeName).Append('=').Append(LdapConnectorUtilities.EscapeLdapFilterValue(value)).Append(')');

        return filter.Append(')').ToString();
    }

    /// <summary>
    /// Searches every partition root in <paramref name="searchBases"/> for the batch, and reports an outcome per
    /// candidate. Never throws for a directory fault: a refused, failed or timed-out search is a
    /// <see cref="UniquenessProbeResult.Failed"/> result naming why, so the synchronisation carries on with JIM's own
    /// records.
    /// </summary>
    internal Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, IReadOnlyList<string> searchBases, CancellationToken cancellationToken)
    {
        request.Validate();

        if (!IsValidAttributeDescription(request.AttributeName))
            return Task.FromResult(UniquenessProbeResult.Failed(request.Candidates.Count,
                $"\"{request.AttributeName}\" is not an LDAP attribute name JIM can probe for"));

        if (searchBases.Count == 0)
            return Task.FromResult(UniquenessProbeResult.Failed(request.Candidates.Count,
                "No partition is selected, so there is nowhere to probe"));

        var filter = BuildFilter(request.AttributeName, request.ControlValue == null ? request.Candidates : request.Candidates.Append(request.ControlValue));
        var valuesFound = new List<string>();

        foreach (var searchBase in searchBases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var searchRequest = new SearchRequest(searchBase, filter, SearchScope.Subtree, request.AttributeName)
            {
                SizeLimit = SizeLimit,
                TimeLimit = request.Timeout
            };

            SearchResponse response;
            try
            {
                response = (SearchResponse)_executor.SendRequest(searchRequest, request.Timeout);
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
            {
                _logger.Warning("LdapConnectorUniquenessProbe: More than {SizeLimit} entries under {SearchBase} hold {Attribute} values in this batch; the attribute is not unique there, so the probe cannot answer for it.",
                    SizeLimit, LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(request.AttributeName));
                return Task.FromResult(UniquenessProbeResult.Undetermined(request.Candidates.Count,
                    $"More than {SizeLimit} entries hold the {request.AttributeName} values probed for, so the attribute is not unique in the directory"));
            }
            catch (DirectoryOperationException ex)
            {
                var resultCode = ex.Response?.ResultCode.ToString() ?? "an unknown result";
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: The search of {SearchBase} for {Attribute} values was refused with {ResultCode}.",
                    LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(request.AttributeName), resultCode);
                return Task.FromResult(UniquenessProbeResult.Failed(request.Candidates.Count,
                    $"The directory refused the probe of {searchBase} with {resultCode}"));
            }
            catch (LdapException ex)
            {
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: The search of {SearchBase} for {Attribute} values failed (LDAP error {ErrorCode}).",
                    LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(request.AttributeName), ex.ErrorCode);
                return Task.FromResult(UniquenessProbeResult.Failed(request.Candidates.Count, DescribeLdapFailure(ex, request.Timeout)));
            }

            if (response.ResultCode != ResultCode.Success)
            {
                return Task.FromResult(UniquenessProbeResult.Failed(request.Candidates.Count,
                    $"The directory answered the probe of {searchBase} with {response.ResultCode}"));
            }

            foreach (SearchResultEntry entry in response.Entries)
                valuesFound.AddRange(LdapConnectorUtilities.GetEntryAttributeStringValues(entry, request.AttributeName) ?? []);
        }

        _logger.Debug("LdapConnectorUniquenessProbe: Searched {PartitionCount} partition(s) for {CandidateCount} {Attribute} candidate(s); {ValueCount} value(s) returned.",
            searchBases.Count, request.Candidates.Count, LogSanitiser.Sanitise(request.AttributeName), valuesFound.Count);

        return Task.FromResult(UniquenessProbeResult.FromValuesFound(request, valuesFound));
    }

    /// <summary>
    /// A sentence for an LDAP client failure: a client-side timeout (85) gets its own wording, since "did not answer
    /// in time" and "could not be reached" lead an administrator to different places.
    /// </summary>
    private static string DescribeLdapFailure(LdapException ex, TimeSpan timeout) =>
        ex.ErrorCode == 85
            ? $"The directory did not answer within {timeout.TotalSeconds:0} seconds"
            : $"The probe failed: {ex.Message.TrimEnd('.')}";

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9-]*|[0-9]+(?:\.[0-9]+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeDescriptionPattern();
}
