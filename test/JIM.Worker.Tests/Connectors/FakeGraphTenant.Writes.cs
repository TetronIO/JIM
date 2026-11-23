// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json.Nodes;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The fake tenant's writes: creating, changing and deleting users and groups, membership and ownership through
/// <c>@odata.bind</c> and <c>$ref</c>, and a user's manager. It answers as Graph does, including the ambiguous 400s a
/// Connector has to tell apart: a member already present, and a member that does not exist.
/// </summary>
internal sealed partial class FakeGraphTenant
{
    /// <summary>When set, consulted before every write; a non-null answer is returned instead of performing it.</summary>
    public Func<HttpMethod, string, JsonNode?, (HttpStatusCode Status, string Code, string Message)?>? WriteInterceptor { get; set; }

    /// <summary>When true, a change to a synchronised object (<c>onPremisesSyncEnabled</c>) is refused, as Graph refuses it.</summary>
    public bool RefusesWritesToSynchronisedObjects { get; set; } = true;

    /// <summary>The id the tenant assigns to the next object created; generated when not set.</summary>
    public Queue<string> NextIds { get; } = new();

    public JsonObject? User(string id) => Users.FirstOrDefault(u => (string)u["id"]! == id);

    public JsonObject? Group(string id) => Groups.FirstOrDefault(g => (string)g["id"]! == id);

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
            _ => Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"No route for {method} {path}.")
        };
    }

    private (int, JsonNode?) CreateUser(JsonObject body)
    {
        if (body["passwordProfile"]?["password"] is null)
            return Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid value specified for property 'passwordProfile' of resource 'User'.");

        var upn = (string?)body["userPrincipalName"];
        if (Users.Any(u => string.Equals((string?)u["userPrincipalName"], upn, StringComparison.OrdinalIgnoreCase)))
            return Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property userPrincipalName already exists.");

        var user = new JsonObject { ["id"] = NextId(), ["onPremisesSyncEnabled"] = null };
        foreach (var (name, value) in body.Where(p => p.Key != "passwordProfile"))
            user[name] = value?.DeepClone();
        Users.Add(user);
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

        return (204, null);
    }

    private (int, JsonNode?) Delete(string collection, string id)
    {
        var removed = (collection == "users" ? Users : Groups).RemoveAll(o => (string)o["id"]! == id);
        return removed == 0
            ? Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{id}' does not exist or one of its queried reference-property objects are not present.")
            : (204, null);
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
        return (204, null);
    }

    private (int, JsonNode?) RemoveReference(string groupId, string relationship, string reference)
    {
        var removed = relationship == "members"
            ? (Members.GetValueOrDefault(groupId)?.RemoveAll(m => m.Id == reference) ?? 0)
            : (Owners.GetValueOrDefault(groupId)?.RemoveAll(o => o == reference) ?? 0);
        if (removed > 0)
            return (204, null);

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

        user["managerId"] = managerId;
        return (204, null);
    }

    private (int, JsonNode?) RemoveManager(string userId)
    {
        var user = User(userId);
        if (user?["managerId"] is null)
            return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource 'manager' does not exist or one of its queried reference-property objects are not present.");

        user.Remove("managerId");
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
            ? Members.GetValueOrDefault(groupId)?.Any(m => m.Id == reference) == true
            : Owners.GetValueOrDefault(groupId)?.Contains(reference) == true;

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
