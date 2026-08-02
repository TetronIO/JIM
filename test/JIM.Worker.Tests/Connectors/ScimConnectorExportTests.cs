// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Connectors.MockScim;
using Serilog;
using ILogger = Serilog.ILogger;
using static JIM.Worker.Tests.Connectors.ScimExportTestObjects;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Export: applying Pending Exports to a service provider. Every test drives the real connector against
/// <see cref="MockScimProvider"/> and asserts on the request that reached the wire, because the failure
/// that matters is a change JIM records as applied that the provider never received in a shape it
/// understood.
/// </summary>
[TestFixture]
public class ScimConnectorExportTests
{
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    private async Task<List<ConnectedSystemExportResult>> ExportAsync(MockScimProvider provider, StubHttpMessageHandler handler, params PendingExport[] pendingExports)
    {
        var connector = new StubbedTransportScimConnector(handler);
        connector.OpenExportConnection([]);

        try
        {
            return await connector.ExportAsync(pendingExports, CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    private static string BodyOf(StubHttpMessageHandler handler, HttpMethod method)
    {
        return handler.Requests.Last(r => r.Method == method).Body ?? string.Empty;
    }

    #region create
    [Test]
    public async Task ExportAsync_Create_PostsTheResourceAndReturnsTheIdTheProviderAssignedAsync()
    {
        // Without the id JIM cannot update or delete the object later, and the confirming import would
        // create a second Connected System Object for the same resource.
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler, Create(user, Change("userName", user, "alice")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"));
            Assert.That(provider.Resources, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ExportAsync_Create_WritesValuesInTheShapeTheSchemaSaysAsync()
    {
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        await ExportAsync(provider, handler,
            Create(user, Change("name.givenName", user, "Alice"), Change("emails.work", user, "alice@example.com")));

        var body = BodyOf(handler, HttpMethod.Post);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("\"name\":{\"givenName\":\"Alice\"}"));
            Assert.That(body, Does.Contain("\"emails\":[{\"value\":\"alice@example.com\",\"type\":\"work\"}]"));
        });
    }

    [Test]
    public async Task ExportAsync_CreateRejectedForSomethingItReferences_IsReportedAsADependencyOrderingProblemAsync()
    {
        // RFC 7644 makes the client responsible for creating dependencies first, so this says the
        // referenced object has not been exported yet rather than that the data is wrong.
        var provider = new MockScimProvider();
        provider.Options.RejectsCreateWithMissingDependency = true;
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler, Create(user, Change("userName", user, "alice")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.MissingDependency));
        });
    }

    [Test]
    public async Task ExportAsync_AttributeTheProviderSchemaDoesNotHave_FailsTheObjectRatherThanExportingPartOfItAsync()
    {
        // Exporting the rest would record the change as applied when part of it never left JIM.
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Create(user, Change("userName", user, "alice"), Change("notAnAttribute", user, "x")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Post), Is.False);
        });
    }
    #endregion

    #region update
    [Test]
    public async Task ExportAsync_Update_SendsAPatchNamingOnlyWhatChangedAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer")));

        var body = BodyOf(handler, HttpMethod.Patch);
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(body, Does.Contain("\"op\":\"replace\"").And.Contain("\"path\":\"title\"").And.Contain("Engineer"));
            Assert.That(body, Does.Not.Contain("userName"), "an attribute JIM did not change is not asserted");
        });
    }

    [Test]
    public async Task ExportAsync_UpdateAgainstAProviderWithoutPatch_ReadsModifiesAndWritesTheWholeResourceAsync()
    {
        // A PUT asserts the entire resource, so one built from JIM's changes alone would clear every
        // attribute the provider holds that JIM does not manage. Reading first is what makes it safe.
        var provider = new MockScimProvider();
        provider.Options.SupportsPatch = false;
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(BodyOf(handler, HttpMethod.Put), Does.Contain("Engineer"));
            // userName was never touched by JIM, and it survives the update.
            Assert.That(provider.Resources.Single().Attributes.Keys, Does.Contain("userName"));
            Assert.That(provider.Resources.Single().Attributes.Keys, Does.Contain("title"));
        });
    }

    [Test]
    public async Task ExportAsync_ReplacingAResourceTheProviderWillNotReturn_FailsRatherThanGuessingItsContentsAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.SupportsPatch = false;
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("ghost", user, PendingExportChangeType.Update, Change("title", user, "Engineer")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put), Is.False);
        });
    }

    [Test]
    public async Task ExportAsync_ResourceChangedBetweenTheReadAndTheWrite_IsReportedAsAConcurrencyConflictAsync()
    {
        // The lost update If-Match exists to prevent: without it JIM would write its own read-modified
        // copy over whatever landed in between, and nothing would say so.
        var provider = new MockScimProvider();
        provider.Options.SupportsPatch = false;
        provider.Options.ChangesVersionBetweenReadAndWrite = true;
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer")));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.ConcurrencyConflict));
        });
    }

    [Test]
    public async Task ExportAsync_PatchAgainstAProviderWithEntityTags_SendsTheTagJimLastImportedAsync()
    {
        var provider = new MockScimProvider();
        var alice = provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var pendingExport = Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer"));
        WithImportedEntityTag(pendingExport, user, alice.Version);

        var results = await ExportAsync(provider, handler, pendingExport);

        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(patch.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null, Is.EqualTo(alice.Version));
        });
    }

    [Test]
    public async Task ExportAsync_PatchWithAStaleEntityTag_IsReportedAsAConcurrencyConflictAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var pendingExport = Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer"));
        WithImportedEntityTag(pendingExport, user, "W/\"stale\"");

        var results = await ExportAsync(provider, handler, pendingExport);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.ConcurrencyConflict));
        });
    }

    [Test]
    public async Task ExportAsync_ProviderWithoutEntityTags_SendsNoIfMatchAsync()
    {
        // A provider that does not maintain entity tags would either ignore If-Match or reject every
        // write carrying one, so JIM does not send one it has no reason to trust.
        var provider = new MockScimProvider();
        provider.Options.SupportsETag = false;
        var alice = provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var pendingExport = Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer"));
        WithImportedEntityTag(pendingExport, user, alice.Version);

        await ExportAsync(provider, handler, pendingExport);

        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.That(patch.Headers.Contains("If-Match"), Is.False);
    }

    [Test]
    public async Task ExportAsync_UpdateOfAnObjectWithNoExternalId_FailsWithoutCallingTheProviderAsync()
    {
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var pendingExport = Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer"));
        pendingExport.ConnectedSystemObject!.AttributeValues.Clear();

        var results = await ExportAsync(provider, handler, pendingExport);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Patch), Is.False);
        });
    }

    [Test]
    public async Task ExportAsync_RemovingAGroupMember_NamesTheMemberGoingRatherThanTheWholeAttributeAsync()
    {
        // "remove members" would take every member with it.
        var provider = new MockScimProvider();
        provider.AddGroup("engineers", "Engineers");
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        await ExportAsync(provider, handler,
            Against("engineers", group, PendingExportChangeType.Update,
                Change("members", group, "alice", PendingExportAttributeChangeType.Remove)));

        Assert.That(BodyOf(handler, HttpMethod.Patch), Does.Contain("members[value eq \\u0022alice\\u0022]"));
    }

    [Test]
    public async Task ExportAsync_AddingAGroupMember_SendsTheReferenceInItsComplexFormAsync()
    {
        var provider = new MockScimProvider();
        provider.AddGroup("engineers", "Engineers");
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        await ExportAsync(provider, handler,
            Against("engineers", group, PendingExportChangeType.Update,
                Change("members", group, "alice", PendingExportAttributeChangeType.Add)));

        var body = BodyOf(handler, HttpMethod.Patch);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("\"op\":\"add\""));
            Assert.That(body, Does.Contain("\"value\":[{\"value\":\"alice\"}]"));
        });
    }
    #endregion

    #region delete
    [Test]
    public async Task ExportAsync_Delete_RemovesTheResourceAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler, Against("alice", user, PendingExportChangeType.Delete));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(provider.Resources, Is.Empty);
        });
    }

    [Test]
    public async Task ExportAsync_DeleteOfAResourceAlreadyGone_SucceedsBecauseTheIntendedStateIsReachedAsync()
    {
        // Failing would leave a Pending Export retrying for ever against a provider that has already
        // done what was asked of it.
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler, Against("ghost", user, PendingExportChangeType.Delete));

        Assert.That(results[0].Success, Is.True);
    }
    #endregion

    #region batches
    [Test]
    public async Task ExportAsync_OneObjectFailing_DoesNotAbandonTheRestOfTheBatchAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("ghost", user, PendingExportChangeType.Update, Change("title", user, "Engineer")),
            Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer")));

        // One result per Pending Export, in the order they arrived: that is how JIM pairs an outcome
        // with the change that produced it.
        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[1].Success, Is.True);
        });
    }

    [Test]
    public async Task ExportAsync_ObjectTypeTheProviderDoesNotPublish_FailsThatObjectOnlyAsync()
    {
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var device = ObjectType("Device");

        var results = await ExportAsync(provider, handler, Create(device, Change("displayName", device, "Printer")));

        Assert.That(results[0].Success, Is.False);
    }

    [Test]
    public void ExportAsync_WithoutOpeningTheConnection_Throws()
    {
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var connector = new StubbedTransportScimConnector(handler);

        Assert.That(async () => await connector.ExportAsync([], CancellationToken.None, new RecordingConnectorProgress()),
            Throws.TypeOf<InvalidOperationException>());
    }
    #endregion
}
