// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An in-memory Microsoft Entra ID tenant answering the Microsoft Graph requests the Connector makes: the token
/// endpoint, paged user and group lists, single objects, group members (directly and through <c>$batch</c>), the
/// cloud-managed query, delta baselines and delta rounds. Pages honour <c>$top</c> and continue through
/// <c>$skiptoken</c>, as Graph's do.
/// </summary>
/// <remarks>
/// A delta round returns what was recorded through <see cref="RecordChange"/> and <see cref="RecordRemoval"/> since the
/// round's link was issued, in the order recorded, and issues a new link at its end, as Graph does. Each recorded entry
/// is reported separately, so an object recorded twice appears twice, as a group whose member changes span pages does.
/// </remarks>
internal sealed class FakeGraphTenant
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    public List<JsonObject> Users { get; } = [];

    public List<JsonObject> Groups { get; } = [];

    /// <summary>Each group's members by id, each a (type, id) pair: "user", "group", or anything else Graph holds.</summary>
    public Dictionary<string, List<(string Type, string Id)>> Members { get; } = [];

    public Dictionary<string, List<string>> Owners { get; } = [];

    /// <summary>Ids the cloud-managed query reports for users and for groups.</summary>
    public HashSet<string> CloudManaged { get; } = [];

    /// <summary>When set, the cloud-managed query answers with this status instead.</summary>
    public HttpStatusCode? CloudManagedQueryStatus { get; set; }

    /// <summary>Group ids whose members request fails with this status.</summary>
    public Dictionary<string, HttpStatusCode> MembersFailures { get; } = [];

    /// <summary>The page size Graph applies to members, below its usual 100 so paging can be tested.</summary>
    public int MembersPageSize { get; set; } = 100;

    public string[] Roles { get; set; } = GraphTestTokens.AllPermissions;

    /// <summary>How many entries a delta page holds.</summary>
    public int DeltaPageSize { get; set; } = 100;

    /// <summary>Delta tokens Graph no longer honours, and the status and error code it answers them with.</summary>
    public Dictionary<string, (HttpStatusCode Status, string Code)> ExpiredDeltaTokens { get; } = [];

    /// <summary>
    /// Ids a single-object read cannot find yet, and how many more reads will not find it, as replication delay leaves
    /// an object just created or changed.
    /// </summary>
    public Dictionary<string, int> NotYetReadable { get; } = [];

    /// <summary>Group ids whose members were requested, in order: hidden membership must never be asked for without the permission.</summary>
    public List<string> MembersRequested { get; } = [];

    private readonly Dictionary<string, List<JsonObject>> _deltaEntries = new() { ["users"] = [], ["groups"] = [] };
    private readonly Dictionary<string, int> _deltaTokenPositions = [];
    private int _deltaTokenCount;

    public StubHttpMessageHandler Handler { get; }

    public FakeGraphTenant()
    {
        Handler = new StubHttpMessageHandler(Respond);
    }

    public IEnumerable<StubHttpMessageHandler.RecordedRequest> GraphRequests =>
        Handler.Requests.Where(r => r.RequestUri!.Host == "graph.microsoft.com");

    public JsonObject AddUser(string id, string displayName, Action<JsonObject>? configure = null)
    {
        var user = new JsonObject { ["id"] = id, ["displayName"] = displayName, ["userType"] = "Member", ["onPremisesSyncEnabled"] = null };
        configure?.Invoke(user);
        Users.Add(user);
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
        var source = collection == "users" ? Users : Groups;
        source.RemoveAll(o => (string)o["id"]! == id);
    }

    /// <summary>The delta token of the link a round would issue now, so a test can expire it.</summary>
    public static string BaselineToken(string collection) => $"baseline-{collection}";

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (GraphTestTokens.IsTokenRequest(request))
            return GraphTestTokens.TokenResponse(Roles);

        var path = request.RequestUri!.AbsolutePath["/v1.0/".Length..];
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

        if (request.Method == HttpMethod.Post && path == "$batch")
            return Batch(request);

        return Json(Get(path, query));
    }

    private (int Status, JsonNode? Body) Get(string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (path is "users/delta" or "groups/delta")
            return Delta(path.Split('/')[0], query);

        if ((path is "users" or "groups") && query["$filter"]?.Contains("onPremisesSyncBehavior/isCloudManaged", StringComparison.Ordinal) == true)
        {
            if (CloudManagedQueryStatus is { } status)
                return ((int)status, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_UnsupportedQuery", ["message"] = "Unsupported query." } });

            var source = path == "users" ? Users : Groups;
            var ids = source.Select(o => (string)o["id"]!).Where(CloudManaged.Contains).Select(id => (JsonNode)new JsonObject { ["id"] = id });
            return (200, new JsonObject { ["value"] = new JsonArray(ids.ToArray()) });
        }

        if (path is "users" or "groups")
            return (200, Page(path, path == "users" ? Users : Groups, query, o => Project(o, query, path)));

        var parts = path.Split('/');
        if (parts is ["users" or "groups", var objectId])
            return ReadOne(parts[0], objectId, query);

        if (parts is ["groups", var groupId, "members"])
        {
            MembersRequested.Add(groupId);
            if (MembersFailures.TryGetValue(groupId, out var failure))
                return ((int)failure, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = "Not found." } });

            var members = Members.GetValueOrDefault(groupId) ?? [];
            var top = Math.Min(int.Parse(query["$top"] ?? "100"), MembersPageSize);
            return (200, PageOf($"groups/{groupId}/members", members.Select(m => (JsonNode)new JsonObject { ["@odata.type"] = $"#microsoft.graph.{m.Type}", ["id"] = m.Id }).ToList(), top, query["$skiptoken"]));
        }

        if (parts is ["groups", var ownedGroupId, "owners"])
        {
            var owners = Owners.GetValueOrDefault(ownedGroupId) ?? [];
            return (200, PageOf($"groups/{ownedGroupId}/owners", owners.Select(o => (JsonNode)new JsonObject { ["@odata.type"] = "#microsoft.graph.user", ["id"] = o }).ToList(), 100, query["$skiptoken"]));
        }

        return (404, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound", ["message"] = $"No route for {path}." } });
    }

    /// <summary>
    /// A delta round. <c>$deltatoken=latest</c> issues a link at the tenant's present without returning anything; any
    /// other token returns what was recorded after its link was issued.
    /// </summary>
    private (int Status, JsonNode? Body) Delta(string collection, System.Collections.Specialized.NameValueCollection query)
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
            return (400, new JsonObject { ["error"] = new JsonObject { ["code"] = "badRequest", ["message"] = "This fake only starts rounds from a token." } });
        }

        var page = entries.Skip(start).Take(deltaToken == "latest" ? 0 : DeltaPageSize).Select(e => (JsonNode)e.DeepClone()).ToArray();
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

    private (int Status, JsonNode? Body) ReadOne(string collection, string id, System.Collections.Specialized.NameValueCollection query)
    {
        var graphObject = (collection == "users" ? Users : Groups).FirstOrDefault(o => (string)o["id"]! == id);
        if (NotYetReadable.TryGetValue(id, out var misses) && misses > 0)
        {
            NotYetReadable[id] = misses - 1;
            graphObject = null;
        }

        return graphObject == null
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
            var (status, answer) = Get(url.AbsolutePath["/v1.0/".Length..], HttpUtility.ParseQueryString(url.Query));
            responses.Add(new JsonObject { ["id"] = item["id"]!.GetValue<string>(), ["status"] = status, ["body"] = answer });
        }

        return Json((200, new JsonObject { ["responses"] = responses }));
    }

    private static JsonObject Page(string path, List<JsonObject> source, System.Collections.Specialized.NameValueCollection query, Func<JsonObject, JsonNode> project)
    {
        var top = int.Parse(query["$top"] ?? "100");
        return PageOf(path, source.Select(project).ToList(), top, query["$skiptoken"], query);
    }

    private static JsonObject PageOf(string path, List<JsonNode> items, int top, string? skipToken, System.Collections.Specialized.NameValueCollection? query = null)
    {
        var offset = skipToken == null ? 0 : int.Parse(skipToken);
        var page = new JsonObject { ["value"] = new JsonArray(items.Skip(offset).Take(top).Select(i => i.DeepClone()).ToArray()) };
        if (offset + top < items.Count)
        {
            var carried = query == null ? "" : string.Concat(query.AllKeys.Where(k => k != "$skiptoken").Select(k => $"&{k}={Uri.EscapeDataString(query[k]!)}"));
            page["@odata.nextLink"] = $"{Graph}{path}?$skiptoken={offset + top}{carried}";
        }

        return page;
    }

    /// <summary>Returns only the selected properties, as Graph does, and the manager when it is expanded.</summary>
    private JsonNode Project(JsonObject graphObject, System.Collections.Specialized.NameValueCollection query, string path)
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
