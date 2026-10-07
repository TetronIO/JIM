// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using JIM.Connectors.SCIM.Authentication;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Searches a SCIM service provider for generated values already in use (#1941; Unique Value Generation, #242,
/// release 3). One filtered GET per object at the resource type's endpoint, carrying the object's candidates and the
/// control value as equality assertions joined by <c>or</c> (RFC 7644 section 3.4.2.2), and reading back the probed
/// attribute from whatever comes back.
/// <para>
/// The provider's own equality rules decide what counts as the same value: an attribute that is not case exact (most
/// are, <c>userName</c> included) matches without regard to case. A value the search returns is in use whatever the
/// credential can see; a value it does not return is only as good as the control value coming back with it.
/// </para>
/// </summary>
internal sealed class ScimConnectorUniquenessProbe
{
    /// <summary>
    /// The most values one filter may carry. A batch is at most ten candidates and a control, so one request is the
    /// rule; the limit keeps the filter, and the URL carrying it, within what providers accept should a batch grow.
    /// </summary>
    internal const int MaximumValuesPerRequest = 20;

    /// <summary>
    /// The most resources one request asks for. A probed attribute is meant to be unique, so a batch of at most
    /// eleven values should match a handful of resources; a provider reporting more matches than it returned means
    /// the answer was cut short, and it is not trusted.
    /// </summary>
    internal const int MaximumResults = 100;

    private readonly ScimHttpClient _client;
    private readonly ScimDiscoveryResult _discovery;
    private readonly ILogger _logger;

    internal ScimConnectorUniquenessProbe(ScimHttpClient client, ScimDiscoveryResult discovery, ILogger logger)
    {
        _client = client;
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>
    /// The attribute path a filter compares <paramref name="attribute"/> by, or null when it cannot be searched by
    /// value. A canonical slot (<c>emails.work</c>) is searched across every entry of its attribute
    /// (<c>emails.value</c>): an address held in another entry is still in use, and <c>emails.value eq</c> is far more
    /// widely supported than a value filter. Extension attributes are addressed by their URN (RFC 7644 section 3.10).
    /// </summary>
    internal static string? GetFilterAttributePath(ScimFlattenedAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var prefix = attribute.ExtensionUrn == null ? string.Empty : $"{attribute.ExtensionUrn}:";

        return attribute.Access switch
        {
            ScimValueAccess.Simple => prefix + attribute.SourceAttributeName,
            ScimValueAccess.ComplexSubAttribute or ScimValueAccess.CanonicalSlot when attribute.SubAttributeName != null =>
                $"{prefix}{attribute.SourceAttributeName}.{attribute.SubAttributeName}",
            _ => null
        };
    }

    /// <summary>
    /// The filter for one request: an equality assertion per value, joined by <c>or</c>, each value a quoted JSON
    /// string (see <see cref="QuoteFilterValue"/>).
    /// </summary>
    internal static string BuildFilter(string attributePath, IEnumerable<string> values) =>
        string.Join(" or ", values.Select(value => $"{attributePath} eq {QuoteFilterValue(value)}"));

    /// <summary>
    /// A comparison value as RFC 7644 section 3.4.2.2 has it: a JSON string. The quote and the backslash are escaped,
    /// and control characters written as <c>\u</c> sequences, so whatever a candidate holds it stays one value and can
    /// never become filter grammar. Everything else is left as it is, since a provider's filter parser need not decode
    /// escapes JSON does not require.
    /// </summary>
    internal static string QuoteFilterValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                default:
                    if (char.IsControl(character))
                        quoted.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        quoted.Append(character);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// Searches the provider for the batch and reports an outcome per candidate. Never throws for a provider fault: a
    /// refused, failed or timed-out search is a <see cref="UniquenessProbeResult.Failed"/> result naming why, and a
    /// search that cannot answer for this attribute (a filter the provider will not apply, an answer cut short) is
    /// <see cref="UniquenessProbeResult.Undetermined"/>. Cancellation of the run propagates.
    /// </summary>
    internal async Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, CancellationToken cancellationToken)
    {
        request.Validate();

        var resourceType = _discovery.ResourceTypes.FirstOrDefault(r =>
            string.Equals(r.Name, request.ObjectTypeName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Endpoint));
        if (resourceType == null)
            return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                $"The service provider does not publish a {request.ObjectTypeName} resource type to search");

        var attribute = _discovery.FlattenedAttributes.GetValueOrDefault(resourceType.Name!)?
            .FirstOrDefault(a => string.Equals(a.Name, request.AttributeName, StringComparison.OrdinalIgnoreCase));
        if (attribute == null)
            return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                $"The service provider's {resourceType.Name} schema has no {request.AttributeName} attribute to search");

        var attributePath = GetFilterAttributePath(attribute);
        if (attributePath == null)
            return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                $"{request.AttributeName} is a reference, which cannot be searched by value");

        var values = request.ControlValue == null ? request.Candidates.ToList() : request.Candidates.Append(request.ControlValue).ToList();
        var reader = ReaderFor(attribute);
        var valuesFound = new List<string>();

        // Bounded by the request's own timeout as well as the run's cancellation: the client's retries and backoff
        // could otherwise outlast it, and the search would carry on after the session had stopped waiting.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        foreach (var chunk in values.Chunk(MaximumValuesPerRequest))
        {
            var query = $"{ScimQueryBuilder.NormaliseEndpoint(resourceType.Endpoint!)}?filter={Uri.EscapeDataString(BuildFilter(attributePath, chunk))}&count={MaximumResults}";

            ScimListResponse<JsonElement>? page;
            try
            {
                page = await _client.GetAsync<ScimListResponse<JsonElement>>(query, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Warning("ScimConnectorUniquenessProbe: The search of {Endpoint} for {Attribute} values did not complete within {TimeoutSeconds} seconds.",
                    LogSanitiser.Sanitise(resourceType.Endpoint), LogSanitiser.Sanitise(request.AttributeName), request.Timeout.TotalSeconds);
                return UniquenessProbeResult.Failed(request.Candidates.Count,
                    $"The service provider did not answer within {request.Timeout.TotalSeconds:0} seconds");
            }
            catch (ScimRequestException ex) when (IsFilterRefused(ex))
            {
                _logger.Warning(ex, "ScimConnectorUniquenessProbe: The service provider would not filter {Endpoint} on {Attribute}.",
                    LogSanitiser.Sanitise(resourceType.Endpoint), LogSanitiser.Sanitise(attributePath));
                return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                    $"The service provider would not search {resourceType.Name} resources by {request.AttributeName} (HTTP {(int)ex.StatusCode}{DescribeScimType(ex)})");
            }
            catch (ScimRequestException ex)
            {
                _logger.Warning(ex, "ScimConnectorUniquenessProbe: The search of {Endpoint} for {Attribute} values failed with HTTP {StatusCode}.",
                    LogSanitiser.Sanitise(resourceType.Endpoint), LogSanitiser.Sanitise(request.AttributeName), (int)ex.StatusCode);
                return UniquenessProbeResult.Failed(request.Candidates.Count, DescribeFailure(ex));
            }
            catch (ScimAuthenticationException ex)
            {
                _logger.Warning(ex, "ScimConnectorUniquenessProbe: Could not authenticate with the service provider to probe {Attribute}.",
                    LogSanitiser.Sanitise(request.AttributeName));
                return UniquenessProbeResult.Failed(request.Candidates.Count,
                    $"JIM could not authenticate with the service provider: {LogSanitiser.Sanitise(ex.Message.TrimEnd('.'))}");
            }

            if (page == null)
                return UniquenessProbeResult.Failed(request.Candidates.Count, "The service provider answered the probe with an empty response");

            // A provider reporting more matches than it returned has cut the answer short (a page-size cap, or a
            // filter it did not apply), so a candidate missing from it may simply be on a page JIM did not read.
            if (page.TotalResults > page.Resources.Count)
                return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                    $"The service provider returned {page.Resources.Count} of {page.TotalResults} {resourceType.Name} resources matching the {request.AttributeName} values probed for, so the answer is incomplete");

            var asked = new HashSet<string>(chunk, StringComparer.OrdinalIgnoreCase);
            foreach (var held in page.Resources.Select(reader))
            {
                // Every resource a filter returns holds one of the values asked for. One that holds none means the
                // provider did not apply the filter, so its silence about a candidate proves nothing.
                if (!held.Any(asked.Contains))
                    return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                        $"The service provider did not apply the probe's filter on {request.AttributeName}, returning {resourceType.Name} resources that hold none of the values probed for");

                valuesFound.AddRange(held);
            }
        }

        _logger.Debug("ScimConnectorUniquenessProbe: Searched {Endpoint} for {CandidateCount} {Attribute} candidate(s); {ValueCount} value(s) returned.",
            LogSanitiser.Sanitise(resourceType.Endpoint), request.Candidates.Count, LogSanitiser.Sanitise(request.AttributeName), valuesFound.Count);

        return UniquenessProbeResult.FromValuesFound(request, valuesFound);
    }

    /// <summary>
    /// Reads the probed attribute's values out of a returned resource, through the same reader import uses. A
    /// canonical slot is read across every entry of its attribute, matching the filter <see cref="GetFilterAttributePath"/>
    /// sends, and every value is read as text whatever the schema calls it.
    /// </summary>
    private static Func<JsonElement, List<string>> ReaderFor(ScimFlattenedAttribute attribute)
    {
        var access = attribute.Access == ScimValueAccess.CanonicalSlot ? ScimValueAccess.ComplexSubAttribute : attribute.Access;
        var widened = new ScimFlattenedAttribute(attribute.Name, attribute.ScimPath, AttributeDataType.Text, AttributePlurality.MultiValued,
            required: false, attribute.Writability, attribute.ClassName, access: access, sourceAttributeName: attribute.SourceAttributeName,
            subAttributeName: attribute.SubAttributeName, extensionUrn: attribute.ExtensionUrn);
        IReadOnlyList<ScimFlattenedAttribute> attributes = [widened];

        return resource => ScimResourceReader.Read(resource, attributes).Attributes.SelectMany(a => a.StringValues).ToList();
    }

    /// <summary>
    /// Whether the provider refused the filter itself rather than the request: 400 with no SCIM error type or with
    /// <c>invalidFilter</c>, or 501. A provider that will not filter on this attribute may filter on another, so this
    /// is reported against the attribute alone.
    /// </summary>
    private static bool IsFilterRefused(ScimRequestException exception) =>
        exception.StatusCode == HttpStatusCode.NotImplemented
        || (exception.StatusCode == HttpStatusCode.BadRequest
            && (exception.ScimType == null || string.Equals(exception.ScimType, ScimErrorTypes.InvalidFilter, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A sentence for a failed search. A refused credential and an unreachable provider lead an administrator to
    /// different places, so each says which it was.
    /// </summary>
    private static string DescribeFailure(ScimRequestException exception) =>
        exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"The service provider refused the probe (HTTP {(int)exception.StatusCode}{DescribeScimType(exception)}), so the account JIM connects with may not be allowed to search it",
            // No response arrived at all: the client reports a transport failure as 503 with the cause inside.
            HttpStatusCode.ServiceUnavailable when exception.InnerException != null =>
                $"The service provider could not be reached: {LogSanitiser.Sanitise(exception.InnerException.Message.TrimEnd('.'))}",
            _ => $"The service provider failed the probe (HTTP {(int)exception.StatusCode}{DescribeScimType(exception)})"
        };

    private static string DescribeScimType(ScimRequestException exception) =>
        exception.ScimType == null ? string.Empty : $", {LogSanitiser.Sanitise(exception.ScimType)}";
}
