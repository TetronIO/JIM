// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json.Nodes;
using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestScimServiceProvider;
using static JIM.Worker.Tests.Connectors.ScimExportTestObjects;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Exporting group membership to a provider that will not take it all in one request (#2094). Keycloak refuses members
/// on a Group create, caps a PATCH at 100 operations without advertising it, and ignores an unfiltered removal; each
/// test turns on the one behaviour it is about.
/// </summary>
[TestFixture]
public class ScimExportMembershipTests
{
    private static async Task<List<ConnectedSystemExportResult>> ExportAsync(HttpMessageHandler handler, params PendingExport[] pendingExports)
    {
        var connector = new StubbedTransportScimConnector(handler);
        connector.OpenExportConnection([], persistedConnectorData: null);

        try
        {
            return await connector.ExportAsync(pendingExports, CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    private static PendingExportAttributeValueChange[] Members(ConnectedSystemObjectType group, int count, PendingExportAttributeChangeType changeType) =>
        Enumerable.Range(1, count).Select(i => Change("members", group, $"user-{i}", changeType)).ToArray();

    private static List<string> MembersOf(MockScimResource resource) =>
        (resource.Attributes.GetValueOrDefault("members") as JsonArray)?.Select(m => m!["value"]!.GetValue<string>()).ToList() ?? [];

    private static int OperationCount(StubHttpMessageHandler.RecordedRequest request) =>
        (JsonNode.Parse(request.Body!)!["Operations"] as JsonArray)!.Count;

    #region create
    [Test]
    public async Task ExportAsync_CreateGroupWithMembers_PostsWithoutMembersThenAddsThemByPatchAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.RejectsMembersOnGroupWrite = true;
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Create(group, [Change("displayName", group, "Engineers"), .. Members(group, 2, PendingExportAttributeChangeType.Add)]));

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"));
            Assert.That(post.Body, Does.Not.Contain("members"));
            Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Patch).RequestUri!.AbsolutePath, Does.EndWith("/Groups/generated-1"));
            Assert.That(MembersOf(provider.Resources.Single()), Is.EquivalentTo(new[] { "user-1", "user-2" }));
        }
    }

    [Test]
    public async Task ExportAsync_CreateGroupWhoseMemberPatchFails_StillReportsTheCreateWithItsIdAsync()
    {
        // The group exists once the POST succeeds. Reporting the create as failed would lose its id, and the retry would
        // POST a second group: Keycloak refuses it on displayName, a less strict provider duplicates it. The members
        // missing from it are re-sent as an update once the confirming import has found the group.
        var provider = new MockScimProvider();
        using var handler = new StubHttpMessageHandler(request => request.Method == HttpMethod.Patch
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(string.Empty) }
            : provider.Respond(request));
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Create(group, [Change("displayName", group, "Engineers"), .. Members(group, 2, PendingExportAttributeChangeType.Add)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"));
            Assert.That(provider.Resources, Has.Count.EqualTo(1));
            Assert.That(MembersOf(provider.Resources.Single()), Is.Empty);
        }
    }

    [Test]
    public async Task ExportAsync_CreateGroupWithMembersAgainstAProviderWithoutPatch_PostsTheMembersAsync()
    {
        // With no PATCH there is no way to add them afterwards, so the single POST stays.
        var provider = new MockScimProvider();
        provider.Options.SupportsPatch = false;
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Create(group, [Change("displayName", group, "Engineers"), .. Members(group, 2, PendingExportAttributeChangeType.Add)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Post).Body, Does.Contain("members"));
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Patch), Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_CreateUser_SendsOneRequestAsBeforeAsync()
    {
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        await ExportAsync(handler, Create(user, Change("userName", user, "alice")));

        Assert.That(handler.Requests.Count(r => r.Method != HttpMethod.Get), Is.EqualTo(1));
    }
    #endregion

    #region update
    [Test]
    public async Task ExportAsync_ManyMembersAdded_AreSentAsOneOperationAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.MaximumPatchOperations = 100;
        provider.AddGroup("engineers", "Engineers");
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Against("engineers", group, PendingExportChangeType.Update, Members(group, 150, PendingExportAttributeChangeType.Add)));

        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(OperationCount(patch), Is.EqualTo(1));
            Assert.That(MembersOf(provider.Resources.Single()), Has.Count.EqualTo(150));
        }
    }

    [Test]
    public async Task ExportAsync_MoreThanOneHundredOperations_AreSentInRequestsOfAtMostOneHundredAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.MaximumPatchOperations = 100;
        var held = provider.AddGroup("engineers", "Engineers");
        held.Attributes["members"] = new JsonArray(Enumerable.Range(1, 150).Select(i => (JsonNode)new JsonObject { ["value"] = $"user-{i}" }).ToArray());
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Against("engineers", group, PendingExportChangeType.Update, Members(group, 150, PendingExportAttributeChangeType.Remove)));

        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(patches.Select(OperationCount), Is.EqualTo(new[] { 100, 50 }));
            Assert.That(MembersOf(provider.Resources.Single()), Is.Empty);
        }
    }

    [Test]
    public async Task ExportAsync_ProviderRefusesTooManyOperations_HalvesAndResendsAsync()
    {
        // A provider's real limit can be lower than JIM's ceiling, and it refuses before applying anything, so a smaller
        // request is safe to send.
        var provider = new MockScimProvider();
        provider.Options.MaximumPatchOperations = 30;
        var held = provider.AddGroup("engineers", "Engineers");
        held.Attributes["members"] = new JsonArray(Enumerable.Range(1, 60).Select(i => (JsonNode)new JsonObject { ["value"] = $"user-{i}" }).ToArray());
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Against("engineers", group, PendingExportChangeType.Update, Members(group, 60, PendingExportAttributeChangeType.Remove)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(MembersOf(provider.Resources.Single()), Is.Empty);
        }
    }

    [Test]
    public async Task ExportAsync_TooManyRefusedForASingleOperation_FailsTheChangeAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.MaximumPatchOperations = 0;
        provider.AddGroup("engineers", "Engineers");
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Against("engineers", group, PendingExportChangeType.Update, Change("displayName", group, "Platform")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Count(r => r.Method == HttpMethod.Patch), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ExportAsync_LaterRequestOfAChunkedUpdateFails_ReportsTheChangeFailedAsync()
    {
        // The earlier requests landed, but the change as a whole did not; the confirming import shows what the provider
        // holds, and re-adding a value it already has changes nothing (RFC 7644 section 3.5.2.1).
        var provider = new MockScimProvider();
        provider.AddGroup("engineers", "Engineers");
        var patches = 0;
        using var handler = new StubHttpMessageHandler(request => request.Method == HttpMethod.Patch && ++patches == 2
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(string.Empty) }
            : provider.Respond(request));
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Against("engineers", group, PendingExportChangeType.Update, Members(group, 150, PendingExportAttributeChangeType.Remove)));

        Assert.That(results[0].Success, Is.False);
    }

    [Test]
    public async Task ExportAsync_ChunkedUpdate_SendsTheEntityTagOnlyWithTheFirstRequestAsync()
    {
        // The first request moves the resource's version on, so a tag on the second would always be stale.
        var provider = new MockScimProvider();
        var held = provider.AddGroup("engineers", "Engineers");
        held.Attributes["members"] = new JsonArray(Enumerable.Range(1, 150).Select(i => (JsonNode)new JsonObject { ["value"] = $"user-{i}" }).ToArray());
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");
        var pendingExport = Against("engineers", group, PendingExportChangeType.Update, Members(group, 150, PendingExportAttributeChangeType.Remove));
        WithImportedEntityTag(pendingExport, group, held.Version);

        var results = await ExportAsync(handler, pendingExport);

        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(patches[0].Headers.Contains("If-Match"), Is.True);
            Assert.That(patches[1].Headers.Contains("If-Match"), Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_RemoveAll_IsSentAsARemoveNamingTheAttributeAloneAsync()
    {
        // RFC 7644 section 3.5.2.2 clears an attribute with a path-only remove. A replace on the unfiltered members path
        // only ever adds on Keycloak (keycloak/keycloak#52752).
        var provider = new MockScimProvider();
        provider.AddGroup("engineers", "Engineers").Attributes["members"] = new JsonArray(new JsonObject { ["value"] = "alice" });
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(handler,
            Against("engineers", group, PendingExportChangeType.Update, Change("members", group, "alice", PendingExportAttributeChangeType.RemoveAll)));

        var operation = (JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Patch).Body!)!["Operations"] as JsonArray)!.Single()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(operation["op"]!.GetValue<string>(), Is.EqualTo("remove"));
            Assert.That(operation["path"]!.GetValue<string>(), Is.EqualTo("members"));
            Assert.That(operation["value"], Is.Null);
        }
    }
    #endregion
}
