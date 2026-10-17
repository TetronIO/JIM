// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Connectors.SCIM;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestScimServiceProvider;
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

    private static string BodyOf(StubHttpMessageHandler handler, HttpMethod method)
    {
        return handler.Requests.Last(r => r.Method == method).Body ?? string.Empty;
    }

    #region cancellation
    /// <summary>
    /// #2083: a run cancelled while a request is under way lets that request finish, so a resource the provider has
    /// already created comes back with its id, and reports the changes never sent as not attempted. Throwing instead lost
    /// the id of every resource already created, and the next run posted them again.
    /// </summary>
    [Test]
    public async Task ExportAsync_RunCancelledDuringDispatch_FinishesTheRequestUnderWayAndReportsTheRestNotAttemptedAsync()
    {
        var provider = new MockScimProvider();
        using var run = new CancellationTokenSource();
        using var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
                run.Cancel();
            return provider.Respond(request);
        });
        var user = ObjectType("User");
        var connector = new StubbedTransportScimConnector(handler);
        connector.OpenExportConnection([], persistedConnectorData: null);

        List<ConnectedSystemExportResult> results;
        try
        {
            results = await connector.ExportAsync(
                [Create(user, Change("userName", user, "alice")), Create(user, Change("userName", user, "bob"))],
                run.Token, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, "the request under way finishes");
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"));
            Assert.That(results[1].IsNotAttempted, Is.True);
            Assert.That(provider.Resources, Has.Count.EqualTo(1));
        }
    }
    /// <summary>
    /// #2083: a request still under way once the grace period after a cancellation has run out (a provider throttling
    /// with long Retry-After waits, say) is abandoned rather than left to hold the cancellation up, and reported as a
    /// failed attempt whose outcome is unknown, never as written.
    /// </summary>
    [Test]
    public async Task ExportAsync_RequestStillUnderWayWhenTheGracePeriodRunsOut_IsAbandonedAndReportedFailedAsync()
    {
        var provider = new MockScimProvider();
        using var run = new CancellationTokenSource();
        using var handler = new HangingOnWriteHandler(provider, run);
        var user = ObjectType("User");
        var connector = new StubbedTransportScimConnector(handler) { ExportInFlightGracePeriod = TimeSpan.Zero };
        connector.OpenExportConnection([], persistedConnectorData: null);

        List<ConnectedSystemExportResult> results;
        try
        {
            results = await connector.ExportAsync(
                [Create(user, Change("userName", user, "alice")), Create(user, Change("userName", user, "bob"))],
                run.Token, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].IsNotAttempted, Is.False, "it was sent, so it is a failed attempt rather than one never made");
            Assert.That(results[0].ErrorMessage, Does.Contain("stopped waiting"));
            Assert.That(results[1].IsNotAttempted, Is.True);
        }
    }

    /// <summary>
    /// Answers reads from the provider; a write cancels the run and then waits for as long as its request is allowed to.
    /// </summary>
    private sealed class HangingOnWriteHandler(MockScimProvider provider, CancellationTokenSource run) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return provider.Respond(request);

            await run.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return provider.Respond(request);
        }
    }
    #endregion

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"));
            Assert.That(provider.Resources, Has.Count.EqualTo(1));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("\"name\":{\"givenName\":\"Alice\"}"));
            Assert.That(body, Does.Contain("\"emails\":[{\"value\":\"alice@example.com\",\"type\":\"work\"}]"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.MissingDependency));
        }
    }

    [Test]
    public async Task ExportAsync_CreateRejectedAsUniqueness_IsAValueAlreadyInUseThatNamesNoAttributeTheProviderDidNotAsync()
    {
        // The default detail is RFC 7644's own description of uniqueness, which names no attribute: classified,
        // but left for Collision Remediation's other attribution steps (decision 9) rather than guessed.
        var provider = new MockScimProvider();
        provider.Options.RejectsDuplicateUserName = true;
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Create(user, Change("userName", user, "alice")), Create(user, Change("userName", user, "ALICE")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[1].Success, Is.False);
            Assert.That(results[1].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(results[1].RejectedAttributeName, Is.Null);
            Assert.That(results[1].ErrorMessage, Does.Contain("already in use"));
        }
    }

    [Test]
    public async Task ExportAsync_CreateRejectedAsUniquenessNamingTheAttribute_CarriesTheAttributeAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.RejectsDuplicateUserName = true;
        provider.Options.UniquenessDetail = "The attribute 'userName' must be unique; 'alice' is already in use.";
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Create(user, Change("userName", user, "alice")), Create(user, Change("userName", user, "alice")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[1].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(results[1].RejectedAttributeName, Is.EqualTo("userName"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Post), Is.False);
        }
    }
    [Test]
    public async Task ExportAsync_CreateCarryingAPassword_FailsWithoutSendingAnythingAsync()
    {
        // #2091: a password reaches a service provider only through Password Synchronisation, never through an
        // Attribute Flow, and the provider never returns it, so the export could not be confirmed either.
        var provider = new MockScimProvider();
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Create(user, Change("userName", user, "alice"), Change("password", user, "S3cret!")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("Password Synchronisation").And.Contain("'password'"));
            Assert.That(results[0].ErrorMessage, Does.Not.Contain("S3cret!"));
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Post), Is.False);
            Assert.That(handler.Requests.Any(r => (r.Body ?? string.Empty).Contains("S3cret!")), Is.False);
        }
    }
    #endregion

    #region update
    [Test]
    public async Task ExportAsync_UpdateCarryingAPassword_FailsWithoutSendingAPatchAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, Change("password", user, "S3cret!")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("Password Synchronisation"));
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Patch), Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_UpdateCarryingAPasswordToAProviderWithoutPatch_FailsWithoutSendingAPutAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.SupportsPatch = false;
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, Change("password", user, "S3cret!")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("Password Synchronisation"));
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put), Is.False);
        }
    }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(body, Does.Contain("\"op\":\"replace\"").And.Contain("\"path\":\"title\"").And.Contain("Engineer"));
            Assert.That(body, Does.Not.Contain("userName"), "an attribute JIM did not change is not asserted");
        }
    }

    [Test]
    public async Task ExportAsync_AMultiValuedSourceWithOneValueChangedIntoASingleValuedAttribute_ReplacesTheValueAsync()
    {
        // What export staging produces for a single-valued attribute is what the provider is sent: one replace
        // carrying the value, whatever the plurality of the Metaverse Attribute it flows from (#2182). An add followed
        // by a path-only remove, applied in order, left the attribute with no value at all.
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");
        var title = Attribute("title", user);
        title.Id = 31;
        title.AttributePlurality = AttributePlurality.SingleValued;

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, [.. StagedEmailChange.StageFor(title)]));

        var body = BodyOf(handler, HttpMethod.Patch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(body, Does.Contain("\"op\":\"replace\"").And.Contain("\"path\":\"title\""));
            Assert.That(body, Does.Not.Contain("\"op\":\"remove\"").And.Not.Contain("\"op\":\"add\""));
            Assert.That((provider.Resources.Single().Attributes["title"] as JsonNode)?.GetValue<string>(),
                Is.EqualTo(StagedEmailChange.NewValue));
        }
    }

    [Test]
    public async Task ExportAsync_AMultiValuedSourceLosingItsOnlyValueIntoASingleValuedAttribute_RemovesTheAttributeAsync()
    {
        // The clear export staging produces for a single-valued attribute is an Update carrying no value. Sent as a
        // replace, its null value was left out of the request entirely, and RFC 7644 section 3.5.2.3 requires a replace
        // to carry one: a strict provider refused it, a lenient one ignored it, and the export retried until it failed.
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice").Attributes["title"] = StagedEmailChange.OldValue;
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");
        var title = Attribute("title", user);
        title.Id = 31;
        title.AttributePlurality = AttributePlurality.SingleValued;

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, [.. StagedEmailChange.StageClearFor(title)]));

        var body = BodyOf(handler, HttpMethod.Patch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(body, Does.Contain("\"op\":\"remove\"").And.Contain("\"path\":\"title\""));
            Assert.That(body, Does.Not.Contain("\"op\":\"replace\""));
            Assert.That(provider.Resources.Single().Attributes.ContainsKey("title"), Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_ClearingAMultiValuedReference_RemovesTheWholeAttributeRatherThanNamingAValueAsync()
    {
        // An Update carrying no value against a multi-valued attribute says it should hold nothing. The path-only
        // remove that says so must not name a value: "members[value eq \"\"]" matches no member and removes nothing.
        var provider = new MockScimProvider();
        provider.AddGroup("engineers", "Engineers").Attributes["members"] = new JsonArray(new JsonObject { ["value"] = "alice" });
        using var handler = provider.CreateHandler();
        var group = ObjectType("Group");

        var results = await ExportAsync(provider, handler,
            Against("engineers", group, PendingExportChangeType.Update, Change("members", group, null)));

        var body = BodyOf(handler, HttpMethod.Patch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(body, Does.Contain("\"op\":\"remove\"").And.Contain("\"path\":\"members\""));
            Assert.That(body, Does.Not.Contain("value eq"));
            Assert.That(provider.Resources.Single().Attributes.ContainsKey("members"), Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_Update_ThePatchedValueIsReadableBackFromTheProviderAsync()
    {
        // The integration scenario's confirming import reads the provider after an export, so the mock
        // has to actually apply a simple PATCH; a provider that only acknowledged one would make every
        // exported change unconfirmable, failing the confirming import rather than the export.
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update, Change("title", user, "Engineer")));

        Assert.That((provider.Resources.Single().Attributes["title"] as JsonNode)?.GetValue<string>(), Is.EqualTo("Engineer"));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(BodyOf(handler, HttpMethod.Put), Does.Contain("Engineer"));
            // userName was never touched by JIM, and it survives the update.
            Assert.That(provider.Resources.Single().Attributes.Keys, Does.Contain("userName"));
            Assert.That(provider.Resources.Single().Attributes.Keys, Does.Contain("title"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put), Is.False);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.ConcurrencyConflict));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(patch.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null, Is.EqualTo(alice.Version));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.ConcurrencyConflict));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Patch), Is.False);
        }
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
    public async Task ExportAsync_RemovingOneValueOfAMultiValuedExtensionString_NamesTheValueGoingRatherThanTheWholeAttributeAsync()
    {
        // "remove ...:badges" would take every badge with it, including the one added earlier in the same request
        // (#2181). The provider is left holding exactly the badge JIM kept and the one it added.
        var provider = new MockScimProvider();
        provider.Options.PublishesCustomUserExtension = true;
        provider.AddUser("alice", "alice").Attributes[MockScimProvider.CustomUserExtensionUrn] =
            new JsonObject { ["badges"] = new JsonArray("gold", "silver") };
        using var handler = provider.CreateHandler();
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Against("alice", user, PendingExportChangeType.Update,
                Change("custom.badges", user, "platinum", PendingExportAttributeChangeType.Add),
                Change("custom.badges", user, "gold", PendingExportAttributeChangeType.Remove)));

        var badges = (provider.Resources.Single().Attributes[MockScimProvider.CustomUserExtensionUrn] as JsonObject)?["badges"] as JsonArray;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(BodyOf(handler, HttpMethod.Patch), Does.Contain("badges[value eq \\u0022gold\\u0022]"));
            Assert.That(badges?.Select(b => b?.GetValue<string>()), Is.EqualTo(new[] { "silver", "platinum" }));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("\"op\":\"add\""));
            Assert.That(body, Does.Contain("\"value\":[{\"value\":\"alice\"}]"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(provider.Resources, Is.Empty);
        }
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
    /// <summary>
    /// #2197: a request that fails in a way the provider did not answer (the transport, or an authentication refresh,
    /// failing unexpectedly) fails that change alone. Escaping the batch instead lost the results of every request
    /// already made, so the resources already created were posted again.
    /// </summary>
    [Test]
    public async Task ExportAsync_RequestFailingUnexpectedly_FailsThatChangeAndKeepsTheResultsAlreadyMadeAsync()
    {
        var provider = new MockScimProvider();
        var posts = 0;
        using var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && ++posts == 2)
                throw new InvalidOperationException("The authentication token could not be refreshed.");
            return provider.Respond(request);
        });
        var user = ObjectType("User");

        var results = await ExportAsync(provider, handler,
            Create(user, Change("userName", user, "alice")),
            Create(user, Change("userName", user, "bob")),
            Create(user, Change("userName", user, "carol")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"), "the resource already created keeps its id");
            Assert.That(results[1].Success, Is.False);
            Assert.That(results[1].ErrorMessage, Does.Contain("authentication token could not be refreshed"));
            Assert.That(results[2].Success, Is.True);
        }
    }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[1].Success, Is.True);
        }
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
