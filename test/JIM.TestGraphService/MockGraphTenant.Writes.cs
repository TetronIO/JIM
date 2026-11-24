// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json.Nodes;

namespace JIM.TestGraphService;

/// <summary>
/// The fake tenant's writes: creating, changing and deleting users and groups, membership and ownership through
/// <c>@odata.bind</c> and <c>$ref</c>, and a user's manager. It answers as Graph does, including the ambiguous 400s a
/// Connector has to tell apart: a member already present, and a member that does not exist.
/// </summary>
public partial class MockGraphTenant
{
    /// <summary>When set, consulted before every write; a non-null answer is returned instead of performing it.</summary>
    public Func<HttpMethod, string, JsonNode?, (HttpStatusCode Status, string Code, string Message)?>? WriteInterceptor { get; set; }

    /// <summary>When true, a change to a synchronised object (<c>onPremisesSyncEnabled</c>) is refused, as Graph refuses it.</summary>
    public bool RefusesWritesToSynchronisedObjects { get; set; } = true;

    /// <summary>The id the tenant assigns to the next object created; generated when not set.</summary>
    public Queue<string> NextIds { get; } = new();

    /// <summary>User ids by user principal name, so a create can refuse a name already taken without scanning.</summary>
    private readonly Dictionary<string, string> _userPrincipalNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each manager's reports by id, so deleting a manager clears their reports' manager without scanning.</summary>
    private readonly Dictionary<string, HashSet<string>> _reports = [];

    public JsonObject? User(string id) => Users.TryGet(id, out var user) ? user : null;

    public JsonObject? Group(string id) => Groups.TryGet(id, out var group) ? group : null;

    private (int Status, JsonNode? Body) Write(HttpMethod method, string path, JsonNode? body)
    {
        if (WriteInterceptor?.Invoke(method, path, body) is { } intercepted)
            return Error(intercepted.Status, intercepted.Code, intercepted.Message);

        var parts = path.Split('/');
        return (method.Method, parts) switch
        {
            ("POST", ["users"]) => CreateUser(body!.AsObject()),
            ("POST", ["groups"]) => CreateGroup(body!.AsObject()),
            ("PATCH", ["users" or "groups", var id]) => Patch(parts[0], id, body!.AsObject()),
            ("DELETE", ["users" or "groups", var id]) => Delete(parts[0], id),
            ("POST", ["groups", var id, "members" or "owners", "$ref"]) => AddReferences(id, parts[2], [Referenced(body!["@odata.id"]!.GetValue<string>())]),
            ("DELETE", ["groups", var id, "members" or "owners", var memberId, "$ref"]) => RemoveReference(id, parts[2], memberId),
            ("PUT", ["users", var id, "manager", "$ref"]) => SetManager(id, Referenced(body!["@odata.id"]!.GetValue<string>())),
            ("DELETE", ["users", var id, "manager", "$ref"]) => RemoveManager(id),
            // The tenant holds no directory extensions.
            ("POST", ["directoryObjects", "getAvailableExtensionProperties"]) => (200, new JsonObject { ["value"] = new JsonArray() }),
            _ => Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"No route for {method} {path}.")
        };
    }

    private (int, JsonNode?) CreateUser(JsonObject body)
    {
        if (body["passwordProfile"]?["password"] is null)
            return Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid value specified for property 'passwordProfile' of resource 'User'.");

        if (UserPrincipalNameTaken((string?)body["userPrincipalName"]))
            return Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property userPrincipalName already exists.");

        var user = new JsonObject { ["id"] = NextId(), ["onPremisesSyncEnabled"] = null };
        foreach (var (name, value) in body.Where(p => p.Key != "passwordProfile"))
            user[name] = value?.DeepClone();
        Users.Add(user);
        IndexUserPrincipalName(user);
        Recorded("users", (string)user["id"]!);
        return (201, user.DeepClone());
    }

    private (int, JsonNode?) CreateGroup(JsonObject body)
    {
        // Graph answers a create binding an object it cannot find with 404, not the 400 a member add gives, and makes nothing.
        var binds = Binds(body).ToList();
        var missing = binds.SelectMany(b => b.Urls).Select(Referenced).FirstOrDefault(r => !CanReference(r));
        if (missing != null)
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{missing}' does not exist or one of its queried reference-property objects are not present.");

        var group = new JsonObject { ["id"] = NextId(), ["onPremisesSyncEnabled"] = null };
        var id = (string)group["id"]!;
        foreach (var (name, value) in body.Where(p => !p.Key.EndsWith("@odata.bind", StringComparison.Ordinal)))
            group[name] = value?.DeepClone();

        Members[id] = [];
        Owners[id] = [];
        Recorded("groups", id);
        foreach (var (relationship, urls) in binds)
        {
            foreach (var reference in urls.Select(Referenced))
                AddReference(id, relationship, reference);
        }

        Groups.Add(group);
        return (201, group.DeepClone());
    }

    private (int, JsonNode?) Patch(string collection, string id, JsonObject body)
    {
        var target = collection == "users" ? User(id) : Group(id);
        if (target == null)
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{id}' does not exist or one of its queried reference-property objects are not present.");

        var properties = body.Where(p => !p.Key.EndsWith("@odata.bind", StringComparison.Ordinal)).ToList();
        if (RefusesWritesToSynchronisedObjects && properties.Count > 0 && target["onPremisesSyncEnabled"]?.GetValue<bool>() == true)
            return Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Unable to update the specified properties for on-premises mastered Directory Sync objects or objects currently undergoing migration.");

        foreach (var (relationship, urls) in Binds(body))
        {
            var result = AddReferences(id, relationship, urls.Select(Referenced).ToList());
            if (result.Status != 204)
                return result;
        }

        foreach (var (name, value) in properties)
        {
            if (name == "onPremisesExtensionAttributes" && value is JsonObject extensions)
            {
                var held = target[name] as JsonObject ?? new JsonObject();
                foreach (var (member, memberValue) in extensions)
                    held[member] = memberValue?.DeepClone();
                target[name] = held;
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }

        if (collection == "users")
            IndexUserPrincipalName(target);
        Recorded(collection, id);
        return (204, null);
    }

    private (int, JsonNode?) Delete(string collection, string id)
    {
        if (!Collection(collection).Remove(id))
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{id}' does not exist or one of its queried reference-property objects are not present.");

        if (RecordsWrites)
            RemoveEverywhere(collection, id);

        return (204, null);
    }

    /// <summary>
    /// What Graph does after deleting an object, recorded for the next delta round: the object leaves every group it
    /// was in or owned, its reports lose their manager, and the round reports it removed.
    /// </summary>
    private void RemoveEverywhere(string collection, string id)
    {
        foreach (var (groupId, members) in Members)
        {
            if (members.Remove(id) | (Owners.GetValueOrDefault(groupId)?.Remove(id) ?? false))
                RecordChange("groups", groupId);
        }

        if (_reports.Remove(id, out var reports))
        {
            foreach (var report in reports)
            {
                if (User(report) is { } reportUser && (string?)reportUser["managerId"] == id)
                {
                    reportUser.Remove("managerId");
                    RecordChange("users", report);
                }
            }
        }

        _deltaEntries[collection].Add(new JsonObject { ["id"] = id, ["@removed"] = new JsonObject { ["reason"] = "deleted" } });
    }

    /// <summary>
    /// Adds every reference or none, as Graph does: one already present fails the whole request, as does one that does
    /// not exist.
    /// </summary>
    private (int Status, JsonNode? Body) AddReferences(string groupId, string relationship, List<string> references)
    {
        if (Group(groupId) == null)
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{groupId}' does not exist or one of its queried reference-property objects are not present.");

        var missing = references.FirstOrDefault(r => !CanReference(r));
        if (missing != null)
        {
            return references.Count == 1
                ? Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{missing}' does not exist or one of its queried reference-property objects are not present.")
                : Error(HttpStatusCode.BadRequest, "Request_BadRequest", $"Resource '{missing}' does not exist or one of its queried reference-property objects are not present.");
        }

        if (references.Any(r => Holds(groupId, relationship, r)))
            return Error(HttpStatusCode.BadRequest, "Request_BadRequest", $"One or more added object references already exist for the following modified properties: '{relationship}'.");

        foreach (var reference in references)
            AddReference(groupId, relationship, reference);
        Recorded("groups", groupId);
        return (204, null);
    }

    private (int, JsonNode?) RemoveReference(string groupId, string relationship, string reference)
    {
        var removed = relationship == "members"
            ? Members.GetValueOrDefault(groupId)?.Remove(reference) ?? false
            : Owners.GetValueOrDefault(groupId)?.Remove(reference) ?? false;
        if (removed)
        {
            Recorded("groups", groupId);
            return (204, null);
        }

        // As Graph answers it: a member deleted from the tenant (which also leaves its groups) is refused as a removed
        // reference that does not exist; one that exists but is not a member, as not found.
        return Exists(reference)
            ? Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{reference}' does not exist or one of its queried reference-property objects are not present.")
            : Error(HttpStatusCode.BadRequest, "Request_BadRequest", $"One or more removed object references do not exist for the following modified properties: '{relationship}'.");
    }

    private (int, JsonNode?) SetManager(string userId, string managerId)
    {
        var user = User(userId);
        if (user == null || User(managerId) == null || !CanReference(managerId))
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{(user == null ? userId : managerId)}' does not exist or one of its queried reference-property objects are not present.");

        if ((string?)user["managerId"] is { } previous)
            _reports.GetValueOrDefault(previous)?.Remove(userId);
        user["managerId"] = managerId;
        (_reports.TryGetValue(managerId, out var reports) ? reports : _reports[managerId] = []).Add(userId);
        Recorded("users", userId);
        return (204, null);
    }

    private (int, JsonNode?) RemoveManager(string userId)
    {
        var user = User(userId);
        if (user?["managerId"] is null)
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource 'manager' does not exist or one of its queried reference-property objects are not present.");

        _reports.GetValueOrDefault((string)user["managerId"]!)?.Remove(userId);
        user.Remove("managerId");
        Recorded("users", userId);
        return (204, null);
    }

    private void AddReference(string groupId, string relationship, string reference)
    {
        if (relationship == "members")
            (Members.TryGetValue(groupId, out var members) ? members : Members[groupId] = []).Add((User(reference) != null ? "user" : "group", reference));
        else
            (Owners.TryGetValue(groupId, out var owners) ? owners : Owners[groupId] = []).Add(reference);
    }

    private bool Holds(string groupId, string relationship, string reference) =>
        relationship == "members"
            ? Members.GetValueOrDefault(groupId)?.Contains(reference) == true
            : Owners.GetValueOrDefault(groupId)?.Contains(reference) == true;

    private bool UserPrincipalNameTaken(string? userPrincipalName) =>
        userPrincipalName != null && _userPrincipalNames.TryGetValue(userPrincipalName, out var holder)
        && User(holder) is { } user && string.Equals((string?)user["userPrincipalName"], userPrincipalName, StringComparison.OrdinalIgnoreCase);

    private void IndexUserPrincipalName(JsonObject user)
    {
        if ((string?)user["userPrincipalName"] is { } userPrincipalName)
            _userPrincipalNames[userPrincipalName] = (string)user["id"]!;
    }

    /// <summary>Records a write for the next delta round, when the tenant records its own writes.</summary>
    private void Recorded(string collection, string id)
    {
        if (RecordsWrites)
            RecordChange(collection, id);
    }

    private bool Exists(string id) => User(id) != null || Group(id) != null;

    /// <summary>Whether a write may refer to the object: it exists, and replication has reached it (<see cref="NotYetReferenceable"/>).</summary>
    private bool CanReference(string id)
    {
        if (!Exists(id))
            return false;
        if (!NotYetReferenceable.TryGetValue(id, out var misses) || misses <= 0)
            return true;
        NotYetReferenceable[id] = misses - 1;
        return false;
    }

    private string NextId() => NextIds.Count > 0 ? NextIds.Dequeue() : Guid.NewGuid().ToString(); // random-id: a fake tenant's object id

    private static IEnumerable<(string Relationship, List<string> Urls)> Binds(JsonObject body) =>
        body.Where(p => p.Key.EndsWith("@odata.bind", StringComparison.Ordinal))
            .Select(p => (p.Key[..p.Key.IndexOf('@')], p.Value!.AsArray().Select(u => u!.GetValue<string>()).ToList()));

    /// <summary>The id at the end of a reference URL, i.e. https://graph.microsoft.com/v1.0/directoryObjects/{id}.</summary>
    private static string Referenced(string url) => url.TrimEnd('/')[(url.TrimEnd('/').LastIndexOf('/') + 1)..];

    private static (int, JsonNode?) Error(HttpStatusCode status, string code, string message) =>
        ((int)status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}
