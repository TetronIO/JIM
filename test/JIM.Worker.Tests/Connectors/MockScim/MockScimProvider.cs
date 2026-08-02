// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;

namespace JIM.Worker.Tests.Connectors.MockScim;

/// <summary>
/// An in-memory SCIM 2.0 service provider, good enough to drive the connector against end to end
/// without a network or a container.
/// <para>
/// It exists because the failure modes that matter most are the ones no real provider will reproduce on
/// demand: a cursor expiring mid-walk, a provider advertising filtering and then rejecting it, a change
/// landing in the same second the run started reading, a gateway clock running ahead of the application
/// clock. Every one of those is a switch on <see cref="MockScimProviderOptions"/>, and the defaults
/// describe a conformant provider so a test only says what it is deviating from.
/// </para>
/// </summary>
internal sealed class MockScimProvider
{
    private const string ListResponseSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    private const string BulkResponseSchema = "urn:ietf:params:scim:api:messages:2.0:BulkResponse";
    private const string UserSchemaUrn = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchemaUrn = "urn:ietf:params:scim:schemas:core:2.0:Group";

    /// <summary>Filters the provider understands, which is what RFC 7644 section 3.4.2.2 defines.</summary>
    private static readonly Regex FilterExpression = new(
        """^\s*(?<attribute>[\w.:]+)\s+(?<operator>eq|ne|gt|ge|lt|le)\s+"(?<value>[^"]*)"\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, int> _cursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expiredCursors = new(StringComparer.Ordinal);
    private int _cursorsIssued;
    private int _resourcesCreated;
    private int _resourceRequests;

    public MockScimProviderOptions Options { get; } = new();

    public List<MockScimResource> Resources { get; } = [];

    /// <summary>
    /// Adds a user last modified at the given instant, or at the provider's clock where none is given.
    /// </summary>
    public MockScimResource AddUser(string id, string userName, DateTimeOffset? lastModified = null)
    {
        var resource = new MockScimResource
        {
            Id = id,
            ResourceType = "User",
            LastModified = lastModified ?? Options.ProviderClock
        };

        resource.Attributes["userName"] = userName;
        resource.Attributes["active"] = true;
        Resources.Add(resource);
        return resource;
    }

    public MockScimResource AddGroup(string id, string displayName, DateTimeOffset? lastModified = null)
    {
        var resource = new MockScimResource
        {
            Id = id,
            ResourceType = "Group",
            LastModified = lastModified ?? Options.ProviderClock
        };

        resource.Attributes["displayName"] = displayName;
        Resources.Add(resource);
        return resource;
    }

    /// <summary>
    /// Invalidates every cursor issued so far, so the next page request using one is answered the way a
    /// provider answers an expired cursor. Called between pages by a test driving the import loop.
    /// </summary>
    public void ExpireIssuedCursors()
    {
        foreach (var cursor in _cursors.Keys)
            _expiredCursors.Add(cursor);
    }

    /// <summary>
    /// Wraps the provider in a recording message handler, ready to hand to
    /// <see cref="StubbedTransportScimConnector"/>.
    /// </summary>
    public StubHttpMessageHandler CreateHandler()
    {
        return new StubHttpMessageHandler(Respond);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (Options.RequiredBearerToken != null && !HasRequiredToken(request.Headers.Authorization))
            return Error(HttpStatusCode.Unauthorized, detail: "The request was not authenticated.");

        if (path.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal))
            return Options.PublishesServiceProviderConfig ? ServiceProviderConfig() : NotFound();

        if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
            return Options.PublishesResourceTypes ? ResourceTypes() : NotFound();

        if (path.EndsWith("/Schemas", StringComparison.Ordinal))
            return Options.PublishesSchemas ? Schemas() : NotFound();

        if (path.EndsWith("/Bulk", StringComparison.Ordinal))
            return Bulk(request);

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var collection = segments.Length > 0 ? ResourceTypeOf(segments[^1]) : null;

        if (collection != null)
        {
            return request.Method == HttpMethod.Post
                ? CreateResource(collection, request)
                : ListResources(collection, request.RequestUri.Query);
        }

        // A request against one resource: /Users/{id}.
        var owner = segments.Length > 1 ? ResourceTypeOf(segments[^2]) : null;
        if (owner != null)
            return SingleResource(owner, Uri.UnescapeDataString(segments[^1]), request);

        return NotFound();
    }

    private static string? ResourceTypeOf(string segment)
    {
        return segment switch
        {
            "Users" => "User",
            "Groups" => "Group",
            _ => null
        };
    }

    private bool HasRequiredToken(AuthenticationHeaderValue? authorisation)
    {
        return string.Equals(authorisation?.Parameter, Options.RequiredBearerToken, StringComparison.Ordinal);
    }

    #region discovery
    private HttpResponseMessage ServiceProviderConfig()
    {
        var bulk = new Dictionary<string, object?>(StringComparer.Ordinal) { ["supported"] = Options.SupportsBulk };
        if (Options.BulkMaxOperations.HasValue)
            bulk["maxOperations"] = Options.BulkMaxOperations.Value;
        if (Options.BulkMaxPayloadSize.HasValue)
            bulk["maxPayloadSize"] = Options.BulkMaxPayloadSize.Value;

        var config = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["patch"] = new Dictionary<string, object?> { ["supported"] = Options.SupportsPatch },
            ["filter"] = new Dictionary<string, object?> { ["supported"] = Options.AdvertisesFiltering },
            ["etag"] = new Dictionary<string, object?> { ["supported"] = Options.SupportsETag },
            ["bulk"] = bulk
        };

        return Json(JsonSerializer.Serialize(config));
    }

    private HttpResponseMessage ResourceTypes()
    {
        var resourceTypes = new object[]
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "User", ["name"] = "User", ["endpoint"] = "/Users", ["schema"] = UserSchemaUrn
            },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "Group", ["name"] = "Group", ["endpoint"] = "/Groups", ["schema"] = GroupSchemaUrn
            }
        };

        return Json(JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemas"] = new[] { ListResponseSchema },
            ["totalResults"] = resourceTypes.Length,
            ["Resources"] = resourceTypes
        }));
    }

    /// <summary>
    /// Serves the RFC 7643 core schemas, which is what a conformant provider publishes. Turning
    /// <see cref="MockScimProviderOptions.PublishesSchemas"/> off drives the connector's core-schema
    /// fallback instead, and the two paths should produce the same attributes.
    /// </summary>
    private HttpResponseMessage Schemas()
    {
        var schemas = new[] { ScimCoreSchemas.User(), ScimCoreSchemas.Group(), ScimCoreSchemas.EnterpriseUser() };

        return Json(JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemas"] = new[] { ListResponseSchema },
            ["totalResults"] = schemas.Length,
            ["Resources"] = schemas
        }, ScimJson.Options));
    }
    #endregion

    #region resources
    private HttpResponseMessage ListResources(string resourceType, string query)
    {
        _resourceRequests++;

        if (_resourceRequests <= Options.ThrottleFirstCalls)
            return Throttled();

        if (_resourceRequests == Options.FailWithServerErrorOnRequest)
            return Error(HttpStatusCode.InternalServerError, detail: "The service provider failed to process the request.");

        if (Options.ReturnsMalformedBody)
            return Json("{ this is not JSON");

        var parameters = ParseQuery(query);
        var matching = Resources.Where(r => r.ResourceType == resourceType).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

        if (parameters.TryGetValue("filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            if (Options.RejectsFilters)
                return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidFilter, "Filtering is not supported on this endpoint.");

            if (Options.HonoursFiltering && !TryApplyFilter(filter, matching, out matching))
                return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidFilter, "The filter could not be parsed.");
        }

        return Options.Pagination == MockScimPaginationStyle.Cursor
            ? CursorPage(matching, parameters)
            : IndexPage(matching, parameters);
    }

    private HttpResponseMessage IndexPage(List<MockScimResource> matching, Dictionary<string, string> parameters)
    {
        var startIndex = ReadInteger(parameters, "startIndex") ?? 1;
        var offset = Math.Max(0, startIndex - 1);

        // A resource added ahead of the current position shifts the window back by one, so the resource
        // read at the end of the previous page arrives again at the top of this one.
        if (Options.RepeatsTheLastResourceOnEachPage && offset > 0)
            offset--;

        var page = matching.Skip(offset).Take(PageSize(parameters)).ToList();

        return Json(ListResponse(matching.Count, startIndex, page, parameters, nextCursor: null));
    }

    private HttpResponseMessage CursorPage(List<MockScimResource> matching, Dictionary<string, string> parameters)
    {
        var offset = 0;
        if (parameters.TryGetValue("cursor", out var cursor) && !string.IsNullOrEmpty(cursor))
        {
            if (_expiredCursors.Contains(cursor))
                return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidValue, "The cursor has expired.");

            if (!_cursors.TryGetValue(cursor, out offset))
                return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidValue, "The cursor is not recognised.");
        }

        var page = matching.Skip(offset).Take(PageSize(parameters)).ToList();
        var nextOffset = offset + page.Count;

        string? nextCursor = null;
        if (nextOffset < matching.Count)
        {
            nextCursor = $"cursor-{++_cursorsIssued}";
            _cursors[nextCursor] = nextOffset;
        }

        return Json(ListResponse(matching.Count, offset + 1, page, parameters, nextCursor));
    }

    private int PageSize(Dictionary<string, string> parameters)
    {
        var requested = ReadInteger(parameters, "count") ?? 100;
        return Options.MaximumPageSize.HasValue ? Math.Min(requested, Options.MaximumPageSize.Value) : requested;
    }

    private string ListResponse(
        int totalResults,
        int startIndex,
        List<MockScimResource> page,
        Dictionary<string, string> parameters,
        string? nextCursor)
    {
        var excluded = ReadExcludedAttributes(parameters);
        var resources = page.Select(resource => Render(resource, excluded)).ToArray();

        // Not an envelope at all: the client must fail rather than read this as an empty page.
        if (Options.ReturnsBareArray)
            return JsonSerializer.Serialize(resources);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemas"] = new[] { ListResponseSchema },
            ["startIndex"] = startIndex,
            ["itemsPerPage"] = page.Count,
            [Options.UsesLowerCaseMemberNames ? "resources" : "Resources"] = resources
        };

        if (!Options.OmitsTotalResults)
            body[Options.UsesLowerCaseMemberNames ? "totalresults" : "totalResults"] =
                Options.ReportsPageSizeAsTotalResults ? page.Count : totalResults;

        if (nextCursor != null)
            body["nextCursor"] = nextCursor;

        return JsonSerializer.Serialize(body);
    }

    private static Dictionary<string, object?> Render(MockScimResource resource, HashSet<string> excluded)
    {
        var rendered = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = resource.Id };

        foreach (var attribute in resource.Attributes.Where(a => !excluded.Contains(a.Key)))
            rendered[attribute.Key] = attribute.Value;

        if (!excluded.Contains("meta"))
        {
            rendered["meta"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["resourceType"] = resource.ResourceType,
                ["lastModified"] = Instant(resource.LastModified),
                ["version"] = resource.Version
            };
        }

        return rendered;
    }

    /// <summary>
    /// Applies a last-modified filter, the only comparison delta import needs. Anything else is
    /// reported as unparseable rather than quietly ignored, so a test cannot pass on a filter the
    /// provider never actually applied.
    /// </summary>
    private static bool TryApplyFilter(string filter, List<MockScimResource> candidates, out List<MockScimResource> matching)
    {
        matching = candidates;

        var match = FilterExpression.Match(filter);
        if (!match.Success || !string.Equals(match.Groups["attribute"].Value, "meta.lastModified", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!DateTimeOffset.TryParse(match.Groups["value"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var comparison))
            return false;

        // Resource metadata is published at one-second precision, so the comparison is made at that
        // precision too: a provider that stored sub-second detail would answer differently to itself.
        matching = match.Groups["operator"].Value.ToLowerInvariant() switch
        {
            "gt" => candidates.Where(r => Truncate(r.LastModified) > comparison).ToList(),
            "ge" => candidates.Where(r => Truncate(r.LastModified) >= comparison).ToList(),
            "lt" => candidates.Where(r => Truncate(r.LastModified) < comparison).ToList(),
            "le" => candidates.Where(r => Truncate(r.LastModified) <= comparison).ToList(),
            "eq" => candidates.Where(r => Truncate(r.LastModified) == comparison).ToList(),
            _ => candidates.Where(r => Truncate(r.LastModified) != comparison).ToList()
        };

        return true;
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
    #endregion

    #region writes
    /// <summary>
    /// Creates a resource from a POST body, assigning the id the client then has to record.
    /// </summary>
    private HttpResponseMessage CreateResource(string resourceType, HttpRequestMessage request)
    {
        if (Options.RejectsCreateWithMissingDependency)
            return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidValue, "A referenced resource does not exist.");

        var body = ReadBody(request);
        if (body == null)
            return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidSyntax, "The request body was not valid JSON.");

        var resource = new MockScimResource
        {
            Id = $"generated-{++_resourcesCreated}",
            ResourceType = resourceType,
            LastModified = Options.ProviderClock
        };

        foreach (var member in body.Where(m => !string.Equals(m.Key, "schemas", StringComparison.OrdinalIgnoreCase)))
            resource.Attributes[member.Key] = member.Value?.DeepClone();

        Resources.Add(resource);

        var response = Json(JsonSerializer.Serialize(Render(resource, [])));
        response.StatusCode = HttpStatusCode.Created;
        return response;
    }

    /// <summary>
    /// Answers a request against one resource. PATCH is acknowledged rather than applied: what is under
    /// test is the request the connector composed, and applying SCIM path semantics faithfully enough to
    /// assert against would make the mock the thing being tested.
    /// </summary>
    private HttpResponseMessage SingleResource(string resourceType, string id, HttpRequestMessage request)
    {
        var resource = Resources.FirstOrDefault(r => r.ResourceType == resourceType && string.Equals(r.Id, id, StringComparison.Ordinal));
        if (resource == null)
            return Error(HttpStatusCode.NotFound, detail: "No such resource.");

        if (request.Method == HttpMethod.Delete)
        {
            Resources.Remove(resource);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent(string.Empty) };
        }

        if (request.Method == HttpMethod.Patch && !Options.SupportsPatch)
            return Error(HttpStatusCode.NotImplemented, detail: "PATCH is not supported.");

        // RFC 7644 section 3.14: a write carrying a stale entity tag is refused rather than allowed to
        // overwrite whatever changed the resource in between.
        var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.FirstOrDefault() : null;
        if (request.Method != HttpMethod.Get && ifMatch != null && !string.Equals(ifMatch, resource.Version, StringComparison.Ordinal))
            return Error(HttpStatusCode.PreconditionFailed, detail: "The resource has changed since it was read.");

        if (request.Method == HttpMethod.Put)
        {
            var body = ReadBody(request);
            if (body == null)
                return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidSyntax, "The request body was not valid JSON.");

            resource.Attributes.Clear();
            foreach (var member in body.Where(m => !string.Equals(m.Key, "schemas", StringComparison.OrdinalIgnoreCase)
                                                   && !string.Equals(m.Key, "id", StringComparison.OrdinalIgnoreCase)
                                                   && !string.Equals(m.Key, "meta", StringComparison.OrdinalIgnoreCase)))
            {
                resource.Attributes[member.Key] = member.Value?.DeepClone();
            }
        }

        if (request.Method != HttpMethod.Get)
            resource.LastModified = Options.ProviderClock;

        var response = Json(JsonSerializer.Serialize(Render(resource, [])));
        response.Headers.TryAddWithoutValidation("ETag", resource.Version);

        // Models something else changing the resource between JIM reading it and writing it back, which
        // is the whole reason the entity tag travels with the write.
        if (request.Method == HttpMethod.Get && Options.ChangesVersionBetweenReadAndWrite)
            resource.Version = $"W/\"{Guid.NewGuid()}\"";

        return response;
    }

    /// <summary>
    /// Applies a bulk request (RFC 7644 section 3.7) by replaying each operation through the ordinary
    /// resource handlers, so a bulk export and a per-object export meet exactly the same provider
    /// behaviour: entity tags, missing resources and dependency rejections all answer identically.
    /// </summary>
    private HttpResponseMessage Bulk(HttpRequestMessage request)
    {
        if (!Options.SupportsBulk)
            return Error(HttpStatusCode.NotFound, detail: "This service provider has no bulk endpoint.");

        if (Options.BulkEndpointStatus.HasValue)
            return Error(Options.BulkEndpointStatus.Value, detail: "The bulk endpoint refused the request.");

        var payload = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (Options.BulkMaxPayloadSize.HasValue && Encoding.UTF8.GetByteCount(payload ?? string.Empty) > Options.BulkMaxPayloadSize.Value)
            return Error(HttpStatusCode.RequestEntityTooLarge, detail: "The bulk request was larger than this provider accepts.");

        if (string.IsNullOrWhiteSpace(payload) || JsonNode.Parse(payload) is not JsonObject parsed || parsed["Operations"] is not JsonArray operations)
            return Error(HttpStatusCode.BadRequest, ScimErrorTypes.InvalidSyntax, "The bulk request carried no operations.");

        if (Options.BulkMaxOperations.HasValue && operations.Count > Options.BulkMaxOperations.Value)
            return Error(HttpStatusCode.BadRequest, ScimErrorTypes.TooMany, "The bulk request carried more operations than this provider accepts.");

        var results = operations.OfType<JsonObject>().Select(operation => ApplyBulkOperation(request, operation)).ToList();

        if (Options.BulkOperationsOmittedFromResponse > 0)
            results = results.Take(Math.Max(0, results.Count - Options.BulkOperationsOmittedFromResponse)).ToList();

        if (Options.ReturnsBulkOperationsOutOfOrder)
            results.Reverse();

        return Json(JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemas"] = new[] { BulkResponseSchema },
            ["Operations"] = results
        }));
    }

    private Dictionary<string, object?> ApplyBulkOperation(HttpRequestMessage bulkRequest, JsonObject operation)
    {
        var bulkUri = bulkRequest.RequestUri!;
        var method = operation["method"]?.GetValue<string>() ?? HttpMethod.Post.Method;
        var relativePath = (operation["path"]?.GetValue<string>() ?? string.Empty).TrimStart('/');
        var bulkId = operation["bulkId"]?.GetValue<string>();

        using var inner = new HttpRequestMessage(new HttpMethod(method), new Uri(bulkUri, relativePath));

        // Authentication travels on the bulk request itself, so the replayed operation carries it too.
        inner.Headers.Authorization = bulkRequest.Headers.Authorization;

        if (operation["data"] is { } data)
            inner.Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/scim+json");

        if (operation["version"]?.GetValue<string>() is { } version)
            inner.Headers.TryAddWithoutValidation("If-Match", version);

        using var response = Respond(inner);
        var body = response.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        var parsedBody = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["status"] = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
        };

        if (bulkId != null && !Options.OmitsBulkIdInResponses)
            result["bulkId"] = bulkId;

        if (response.IsSuccessStatusCode)
        {
            var assignedId = (parsedBody as JsonObject)?["id"]?.GetValue<string>();
            result["location"] = assignedId != null && string.Equals(method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase)
                ? new Uri(new Uri(bulkUri, relativePath + "/"), assignedId).AbsoluteUri
                : new Uri(bulkUri, relativePath).AbsoluteUri;
        }
        else
        {
            result["response"] = parsedBody;
        }

        return result;
    }

    private static Dictionary<string, JsonNode?>? ReadBody(HttpRequestMessage request)
    {
        var json = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) is JsonObject parsed
                ? parsed.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
    #endregion

    #region plumbing
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
                continue;

            parameters[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return parameters;
    }

    private static int? ReadInteger(Dictionary<string, string> parameters, string name)
    {
        return parameters.TryGetValue(name, out var value)
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static HashSet<string> ReadExcludedAttributes(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("excludedAttributes", out var value) || string.IsNullOrWhiteSpace(value))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Instant(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private HttpResponseMessage Json(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };

        Stamp(response);
        return response;
    }

    private HttpResponseMessage Throttled()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(string.Empty)
        };

        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
        Stamp(response);
        return response;
    }

    private HttpResponseMessage Error(HttpStatusCode statusCode, string? scimType = null, string? detail = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(ScimError.ForStatus((int)statusCode, detail, scimType), ScimJson.Options),
                Encoding.UTF8,
                "application/scim+json")
        };

        Stamp(response);
        return response;
    }

    private static HttpResponseMessage NotFound()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
    }

    private void Stamp(HttpResponseMessage response)
    {
        if (Options.SendsDateHeader)
            response.Headers.Date = Options.ProviderClock + Options.ClockOffset;
    }
    #endregion
}
