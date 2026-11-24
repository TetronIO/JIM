// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace JIM.TestGraphService;

/// <summary>
/// An in-memory Microsoft Entra ID tenant answering the Microsoft Graph requests the Connector makes: the token
/// endpoint, paged user and group lists, single objects, group members (directly and through <c>$batch</c>), the
/// cloud-managed query, delta baselines and delta rounds. Pages honour <c>$top</c> and continue through
/// <c>$skiptoken</c>, as Graph's do.
/// <para>
/// The unit suite drives it in process, and the integration stack serves it over HTTPS as a stand-in for Microsoft
/// Graph (<see cref="GraphServiceHost"/>), so a scale scenario cannot pass against a more forgiving tenant than the
/// unit tests use. Every lookup is indexed, so a tenant of a million objects answers each request without scanning.
/// </para>
/// </summary>
/// <remarks>
/// A delta round returns what was recorded through <see cref="RecordChange"/> and <see cref="RecordRemoval"/> since the
/// round's link was issued, in the order recorded, and issues a new link at its end, as Graph does. Each recorded entry
/// is reported separately, so an object recorded twice appears twice, as a group whose member changes span pages does.
/// With <see cref="RecordsWrites"/> on, the tenant records its own writes, as Graph does.
/// </remarks>
public partial class MockGraphTenant
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    private readonly Lock _gate = new();

    public GraphObjectList Users { get; } = [];

    public GraphObjectList Groups { get; } = [];

    /// <summary>Each group's members by id, each a (type, id) pair: "user", "group", or anything else Graph holds.</summary>
    public Dictionary<string, MemberList> Members { get; } = [];

    public Dictionary<string, OwnerList> Owners { get; } = [];

    /// <summary>Ids the cloud-managed query reports for users and for groups.</summary>
    public HashSet<string> CloudManaged { get; } = [];

    /// <summary>When set, the cloud-managed query answers with this status instead.</summary>
    public HttpStatusCode? CloudManagedQueryStatus { get; set; }

    /// <summary>Group ids whose members request fails with this status.</summary>
    public Dictionary<string, HttpStatusCode> MembersFailures { get; } = [];

    /// <summary>The page size Graph applies to members, below its usual 100 so paging can be tested.</summary>
    public int MembersPageSize { get; set; } = 100;

    public string[] Roles { get; set; } = GraphTestTokens.AllPermissions;

    /// <summary>Directory roles assigned to the app tenant-wide, as template ids; the token carries them as <c>wids</c>.</summary>
    public string[] DirectoryRoles { get; set; } = [];

    /// <summary>How many entries a delta page holds.</summary>
    public int DeltaPageSize { get; set; } = 100;

    /// <summary>
    /// Whether the tenant records its own writes for the next delta round, and removes a deleted object from its
    /// groups and from its reports' manager, as Graph does. Off for the unit suite, whose tests record exactly the
    /// changes they mean a round to report; on for the stand-in service.
    /// </summary>
    public bool RecordsWrites { get; set; }

    /// <summary>Delta tokens Graph no longer honours, and the status and error code it answers them with.</summary>
    public Dictionary<string, (HttpStatusCode Status, string Code)> ExpiredDeltaTokens { get; } = [];

    /// <summary>
    /// Ids a single-object read cannot find yet, and how many more reads will not find it, as replication delay leaves
    /// an object just created or changed.
    /// </summary>
    public Dictionary<string, int> NotYetReadable { get; } = [];

    /// <summary>
    /// Ids a write cannot yet refer to (as a member, owner or manager), and how many more writes will not find them, as
    /// replication delay leaves an object created moments earlier.
    /// </summary>
    public Dictionary<string, int> NotYetReferenceable { get; } = [];

    /// <summary>Group ids whose members were requested, in order: hidden membership must never be asked for without the permission.</summary>
    public List<string> MembersRequested { get; } = [];

    private readonly Dictionary<string, List<JsonObject>> _deltaEntries = new() { ["users"] = [], ["groups"] = [] };
    private readonly Dictionary<string, int> _deltaTokenPositions = [];
    private int _deltaTokenCount;

    public JsonObject AddUser(string id, string displayName, Action<JsonObject>? configure = null)
    {
        var user = new JsonObject { ["id"] = id, ["displayName"] = displayName, ["userType"] = "Member", ["onPremisesSyncEnabled"] = null };
        configure?.Invoke(user);
        Users.Add(user);
        IndexUserPrincipalName(user);
        return user;
    }

    public JsonObject AddGroup(string id, string displayName, Action<JsonObject>? configure = null)
    {
        var group = new JsonObject { ["id"] = id, ["displayName"] = displayName, ["onPremisesSyncEnabled"] = null };
        configure?.Invoke(group);
        Groups.Add(group);
        return group;
    }

    /// <summary>Records that an object changed, so the next delta round reports it.</summary>
    public void RecordChange(string collection, string id) =>
        _deltaEntries[collection].Add(new JsonObject { ["id"] = id });

    /// <summary>Records that an object was removed, so the next delta round reports it with an <c>@removed</c> annotation.</summary>
    public void RecordRemoval(string collection, string id, string reason = "deleted")
    {
        _deltaEntries[collection].Add(new JsonObject { ["id"] = id, ["@removed"] = new JsonObject { ["reason"] = reason } });
        (collection == "users" ? Users : Groups).Remove(id);
    }

    /// <summary>The delta token of the link a round would issue now, so a test can expire it.</summary>
    public static string BaselineToken(string collection) => $"baseline-{collection}";

    /// <summary>Answers one request, as Microsoft Entra ID's token service or Microsoft Graph would. Safe to call concurrently.</summary>
    public HttpResponseMessage Respond(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (GraphTestTokens.IsTokenRequest(request))
                return GraphTestTokens.TokenResponse(Roles, DirectoryRoles);

            if (!request.RequestUri!.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
                return Json((404, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = "Only Microsoft Graph v1.0 is served." } }));

            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath["/v1.0/".Length..]);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (request.Method == HttpMethod.Post && path == "$batch")
                return Batch(request);

            var body = request.Content == null ? null : ParseBody(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json(Dispatch(request.Method, path, query, body));
        }
    }

    private static JsonNode? ParseBody(string content) => string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);

    private (int Status, JsonNode? Body) Dispatch(HttpMethod method, string path, NameValueCollection query, JsonNode? body) =>
        method == HttpMethod.Get ? Get(path, query) : Write(method, path, body);

    private (int Status, JsonNode? Body) Get(string path, NameValueCollection query)
    {
        if (path is "users/delta" or "groups/delta")
            return Delta(path.Split('/')[0], query);

        if (path is "users/$count" or "groups/$count")
            return (200, JsonValue.Create(Collection(path.Split('/')[0]).Count));

        if ((path is "users" or "groups") && query["$filter"]?.Contains("onPremisesSyncBehavior/isCloudManaged", StringComparison.Ordinal) == true)
        {
            if (CloudManagedQueryStatus is { } status)
                return ((int)status, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_UnsupportedQuery", ["message"] = "Unsupported query." } });

            var source = Collection(path);
            var ids = CloudManaged.Where(source.Contains).Select(id => (JsonNode)new JsonObject { ["id"] = id });
            return (200, new JsonObject { ["value"] = new JsonArray(ids.ToArray()) });
        }

        if (path is "users" or "groups")
        {
            var top = int.Parse(query["$top"] ?? "100");
            if (query["$filter"] is { } filter)
                return Filtered(path, filter, top, query);

            var page = PageOf(path, Collection(path), top, query, o => Project(o, query, path));
            if (query["$count"] == "true")
                page["@odata.count"] = Collection(path).Count;
            return (200, page);
        }

        var parts = path.Split('/');
        if (parts is ["users" or "groups", var objectId])
            return ReadOne(parts[0], objectId, query);

        if (parts is ["users", var reportId, "manager"])
        {
            return User(reportId)?["managerId"] is { } managerId
                ? ReadOne("users", (string)managerId!, query)
                : (404, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = "Resource 'manager' does not exist or one of its queried reference-property objects are not present." } });
        }

        if (parts is ["groups", var countedGroupId, "members", "$count"])
            return (200, JsonValue.Create(Members.GetValueOrDefault(countedGroupId)?.Count ?? 0));

        if (parts is ["groups", var groupId, "members"])
        {
            MembersRequested.Add(groupId);
            if (MembersFailures.TryGetValue(groupId, out var failure))
                return ((int)failure, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = "Not found." } });

            var top = Math.Min(int.Parse(query["$top"] ?? "100"), MembersPageSize);
            var members = Members.GetValueOrDefault(groupId) ?? [];
            var page = PageOf($"groups/{groupId}/members", members, top, query,
                m => new JsonObject { ["@odata.type"] = $"#microsoft.graph.{m.Type}", ["id"] = m.Id });
            if (query["$count"] == "true")
                page["@odata.count"] = members.Count;
            return (200, page);
        }

        if (parts is ["groups", var ownedGroupId, "owners"])
        {
            return (200, PageOf($"groups/{ownedGroupId}/owners", Owners.GetValueOrDefault(ownedGroupId) ?? [], 100, query,
                o => new JsonObject { ["@odata.type"] = "#microsoft.graph.user", ["id"] = o }));
        }

        return (404, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = $"No route for {path}." } });
    }

    private GraphObjectList Collection(string name) => name == "users" ? Users : Groups;

    /// <summary>
    /// A list filtered as Graph filters it, for the two forms the scale scenario's harness asks for: <c>startswith(property,'value')</c>
    /// and <c>property eq 'value'</c>. It scans the collection, as the harness asks rarely; the Connector never filters a list.
    /// </summary>
    private (int Status, JsonNode? Body) Filtered(string path, string filter, int top, NameValueCollection query)
    {
        Func<JsonObject, bool> matches;
        if (System.Text.RegularExpressions.Regex.Match(filter, @"^startswith\((\w+),\s*'([^']*)'\)$") is { Success: true } startsWith)
        {
            var (property, value) = (startsWith.Groups[1].Value, startsWith.Groups[2].Value);
            matches = o => ((string?)o[property])?.StartsWith(value, StringComparison.OrdinalIgnoreCase) == true;
        }
        else if (System.Text.RegularExpressions.Regex.Match(filter, @"^(\w+) eq '([^']*)'$") is { Success: true } equals)
        {
            var (property, value) = (equals.Groups[1].Value, equals.Groups[2].Value);
            matches = o => string.Equals((string?)o[property], value, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return (400, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_UnsupportedQuery", ["message"] = $"This tenant does not filter by {filter}." } });
        }

        var matching = Collection(path).Where(matches).ToList();
        var offset = query["$skiptoken"] is { } skipToken ? int.Parse(skipToken) : 0;
        var page = new JsonObject { ["value"] = new JsonArray(matching.Skip(offset).Take(top).Select(o => Project(o, query, path)).ToArray()) };
        if (query["$count"] == "true")
            page["@odata.count"] = matching.Count;
        if (offset + top < matching.Count)
        {
            var carried = string.Concat(query.AllKeys.Where(k => k != "$skiptoken").Select(k => $"&{k}={Uri.EscapeDataString(query[k]!)}"));
            page["@odata.nextLink"] = $"{Graph}{path}?$skiptoken={offset + top}{carried}";
        }

        return (200, page);
    }

    /// <summary>
    /// A delta round. <c>$deltatoken=latest</c> issues a link at the tenant's present without returning anything; any
    /// other token returns what was recorded after its link was issued.
    /// </summary>
    private (int Status, JsonNode? Body) Delta(string collection, NameValueCollection query)
    {
        var entries = _deltaEntries[collection];
        var deltaToken = query["$deltatoken"];
        int start;
        if (deltaToken == "latest")
        {
            start = entries.Count;
        }
        else if (deltaToken != null)
        {
            if (ExpiredDeltaTokens.TryGetValue(deltaToken, out var expired))
                return ((int)expired.Status, new JsonObject { ["error"] = new JsonObject { ["code"] = expired.Code, ["message"] = "The delta token is no longer valid." } });
            if (!_deltaTokenPositions.TryGetValue(deltaToken, out start))
                return (400, new JsonObject { ["error"] = new JsonObject { ["code"] = "badRequest", ["message"] = $"Unknown delta token {deltaToken}." } });
        }
        else if (query["$skiptoken"] is { } skipToken)
        {
            start = int.Parse(skipToken["d".Length..]);
        }
        else
        {
            return (400, new JsonObject { ["error"] = new JsonObject { ["code"] = "badRequest", ["message"] = "This tenant only starts rounds from a token." } });
        }

        var count = deltaToken == "latest" ? 0 : Math.Min(DeltaPageSize, entries.Count - start);
        var page = entries.GetRange(start, count).Select(e => (JsonNode)e.DeepClone()).ToArray();
        var result = new JsonObject { ["value"] = new JsonArray(page) };
        var next = start + page.Length;
        if (deltaToken != "latest" && next < entries.Count)
        {
            result["@odata.nextLink"] = $"{Graph}{collection}/delta?$skiptoken=d{next}";
            return (200, result);
        }

        // The first link a tenant issues for a collection is the baseline token, so a test can name it.
        var issued = _deltaTokenPositions.ContainsKey(BaselineToken(collection)) ? $"{collection}-{++_deltaTokenCount}" : BaselineToken(collection);
        _deltaTokenPositions[issued] = next;
        result["@odata.deltaLink"] = $"{Graph}{collection}/delta?$deltatoken={issued}";
        return (200, result);
    }

    private (int Status, JsonNode? Body) ReadOne(string collection, string id, NameValueCollection query)
    {
        var found = Collection(collection).TryGet(id, out var graphObject);

        // Graph also finds a user by their user principal name.
        if (!found && collection == "users" && id.Contains('@') && UserPrincipalNameTaken(id))
            found = Users.TryGet(_userPrincipalNames[id], out graphObject);
        if (NotYetReadable.TryGetValue(id, out var misses) && misses > 0)
        {
            NotYetReadable[id] = misses - 1;
            found = false;
        }

        return !found
            ? (404, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = $"Resource '{id}' does not exist." } })
            : (200, Project(graphObject, query, collection));
    }

    private HttpResponseMessage Batch(HttpRequestMessage request)
    {
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
        var responses = new JsonArray();
        foreach (var item in body["requests"]!.AsArray())
        {
            var url = new Uri(new Uri(Graph), item!["url"]!.GetValue<string>().TrimStart('/'));
            var method = new HttpMethod(item["method"]!.GetValue<string>());
            var (status, answer) = Dispatch(method, Uri.UnescapeDataString(url.AbsolutePath["/v1.0/".Length..]), HttpUtility.ParseQueryString(url.Query), item["body"]?.DeepClone());
            responses.Add(new JsonObject { ["id"] = item["id"]!.GetValue<string>(), ["status"] = status, ["body"] = answer });
        }

        return Json((200, new JsonObject { ["responses"] = responses }));
    }

    /// <summary>
    /// A page of up to <paramref name="top"/> items. Its <c>$skiptoken</c> is the position the next page starts from,
    /// which stays valid however many items are removed meanwhile; the rest of the query is carried onto the link.
    /// </summary>
    private static JsonObject PageOf<TKey, TItem>(string path, IndexedList<TKey, TItem> source, int top, NameValueCollection query, Func<TItem, JsonNode> project)
        where TKey : notnull
    {
        var start = query["$skiptoken"] is { } skipToken ? int.Parse(skipToken) : 0;
        var items = source.Page(start, top, out var next);
        var page = new JsonObject { ["value"] = new JsonArray(items.Select(project).ToArray()) };
        if (next < source.Extent)
        {
            var carried = string.Concat(query.AllKeys.Where(k => k != "$skiptoken").Select(k => $"&{k}={Uri.EscapeDataString(query[k]!)}"));
            page["@odata.nextLink"] = $"{Graph}{path}?$skiptoken={next}{carried}";
        }

        return page;
    }

    /// <summary>Returns only the selected properties, as Graph does, and the manager when it is expanded.</summary>
    private static JsonNode Project(JsonObject graphObject, NameValueCollection query, string path)
    {
        var selected = (query["$select"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var projected = new JsonObject();
        foreach (var (name, value) in graphObject)
        {
            if (name == "id" || selected.Contains(name))
                projected[name] = value?.DeepClone();
        }

        if (path == "users" && query["$expand"]?.StartsWith("manager", StringComparison.Ordinal) == true
            && graphObject["managerId"] is { } managerId)
            projected["manager"] = new JsonObject { ["@odata.type"] = "#microsoft.graph.user", ["id"] = managerId.DeepClone() };

        return projected;
    }

    private static HttpResponseMessage Json((int Status, JsonNode? Body) answer) =>
        new((HttpStatusCode)answer.Status) { Content = new StringContent(answer.Body?.ToJsonString() ?? "", Encoding.UTF8, "application/json") };
}
