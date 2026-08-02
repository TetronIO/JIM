// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Full import: walking a service provider's resources page by page and staging each as a Connected
/// System Import Object. JIM drives this by calling the connector until no pagination tokens come back,
/// so the tests walk the same loop.
/// </summary>
[TestFixture]
public class ScimConnectorImportTests
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

    private static ConnectedSystemSettingValue Setting(string name, string? stringValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue
        };
    }

    private static ConnectedSystem ConnectedSystem(
        string paginationMode = ScimConnectorConstants.PaginationModeAuto,
        string? excludedAttributes = null,
        params string[] selectedObjectTypes)
    {
        var objectTypes = (selectedObjectTypes.Length > 0 ? selectedObjectTypes : ["User", "Group"])
            .Select(name => new ConnectedSystemObjectType { Name = name, Selected = true })
            .ToList();

        return new ConnectedSystem
        {
            Name = "SCIM",
            ObjectTypes = objectTypes,
            SettingValues =
            [
                Setting(ScimConnectorConstants.SettingBaseUrl, "https://provider.example.com/scim/v2"),
                Setting(ScimConnectorConstants.SettingPaginationMode, paginationMode),
                Setting(ScimConnectorConstants.SettingExcludedAttributes, excludedAttributes)
            ]
        };
    }

    private static ConnectedSystemRunProfile RunProfile(int pageSize = 2)
    {
        return new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = pageSize };
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };
    }

    private static HttpResponseMessage NotFound()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
    }

    /// <summary>
    /// A provider publishing only the User resource type, whose /Users pages come from the supplied
    /// responder keyed by the page's query string.
    /// </summary>
    private static StubHttpMessageHandler UserProvider(Func<string, HttpResponseMessage> usersResponder, string? resourceTypes = null)
    {
        return new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal))
                return Json("""{ "patch": { "supported": true }, "filter": { "supported": true } }""");

            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
            {
                return Json(resourceTypes ?? """
                { "totalResults": 1, "Resources": [
                    { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" } ] }
                """);
            }

            if (path.EndsWith("/Schemas", StringComparison.Ordinal))
                return NotFound(); // fall back to the RFC 7643 core schemas

            if (path.EndsWith("/Users", StringComparison.Ordinal) || path.EndsWith("/Groups", StringComparison.Ordinal))
                return usersResponder(request.RequestUri.Query);

            return NotFound();
        });
    }

    private static string UserPage(int startIndex, int totalResults, params string[] userNames)
    {
        var resources = string.Join(",", userNames.Select((name, offset) =>
            $$"""{ "id": "{{name}}-id", "userName": "{{name}}", "active": true }"""));

        return $$"""
        { "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
          "totalResults": {{totalResults}}, "startIndex": {{startIndex}}, "itemsPerPage": {{userNames.Length}},
          "Resources": [ {{resources}} ] }
        """;
    }

    private Task<List<ConnectedSystemImportResult>> RunImportAsync(
        StubHttpMessageHandler handler,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        int maximumPages = 10)
    {
        return ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), connectedSystem, runProfile, _logger, maximumPages: maximumPages);
    }

    #region staging
    [Test]
    public async Task ImportAsync_StagesEachResourceAsAnImportObjectOfItsResourceTypeAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 2, "alice", "bob")));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));
        var imported = results.SelectMany(r => r.ImportObjects).ToList();

        Assert.That(imported, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(imported, Has.All.Property(nameof(ConnectedSystemImportObject.ObjectType)).EqualTo("User"));
            Assert.That(imported[0].Attributes.Single(a => a.Name == "userName").StringValues, Is.EqualTo(new[] { "alice" }));
            Assert.That(imported[0].Attributes.Single(a => a.Name == "id").StringValues, Is.EqualTo(new[] { "alice-id" }));
        });
    }

    [Test]
    public async Task ImportAsync_FullImport_MarksEveryObjectAsPresentRatherThanGuessingCreateOrUpdateAsync()
    {
        // A full import asserts the object exists; JIM decides whether that means a create or an update.
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.That(results[0].ImportObjects[0].ChangeType, Is.EqualTo(ObjectChangeType.Added));
    }

    [Test]
    public async Task ImportAsync_ResourceWithAnUnreadableValue_IsStagedCarryingItsErrorAsync()
    {
        // Skipping it would make the object silently absent from the run, which reads as a deletion.
        const string resourceTypes = """
        { "totalResults": 1, "Resources": [
            { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" } ] }
        """;
        using var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
                return Json(resourceTypes);
            if (path.EndsWith("/Schemas", StringComparison.Ordinal))
            {
                return Json("""
                { "totalResults": 1, "Resources": [
                    { "id": "urn:ietf:params:scim:schemas:core:2.0:User", "name": "User",
                      "attributes": [ { "name": "score", "type": "decimal" } ] } ] }
                """);
            }
            if (path.EndsWith("/Users", StringComparison.Ordinal))
                return Json("""{ "totalResults": 1, "Resources": [ { "id": "a", "score": "1e40" } ] }""");
            return NotFound();
        });

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));
        var imported = results[0].ImportObjects.Single();

        Assert.Multiple(() =>
        {
            Assert.That(imported.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(imported.ErrorMessage, Is.Not.Null);
        });
    }

    [Test]
    public async Task ImportAsync_MoreEntriesThanAFlattenedSlotHolds_ReportsAWarningOnTheRunAsync()
    {
        using var handler = UserProvider(_ => Json("""
        { "totalResults": 1, "Resources": [ { "id": "a", "emails": [
            { "value": "one@example.com", "type": "work" },
            { "value": "two@example.com", "type": "work" } ] } ] }
        """));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].WarningMessage, Is.Not.Null);
            Assert.That(results[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.MultiValuedToSingleValued));
            // The object is still imported: the warning is about one value, not the whole object.
            Assert.That(results[0].ImportObjects, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ImportAsync_CleanPage_ReportsNoWarningAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.That(results[0].WarningMessage, Is.Null);
    }
    #endregion

    #region index paging
    [Test]
    public async Task ImportAsync_IndexPaging_WalksEveryPageUntilTheProviderHasNothingLeftAsync()
    {
        using var handler = UserProvider(query => query.Contains("startIndex=1")
            ? Json(UserPage(1, 3, "alice", "bob"))
            : Json(UserPage(3, 3, "carol")));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 2));

        Assert.That(results.SelectMany(r => r.ImportObjects).ToList(), Has.Count.EqualTo(3));
        Assert.That(results, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ImportAsync_IndexPaging_AdvancesTheStartIndexByWhatWasReturnedAsync()
    {
        // Advancing by the requested count instead would skip resources whenever a provider caps the
        // page size below what was asked for.
        using var handler = UserProvider(query => query.Contains("startIndex=1")
            ? Json(UserPage(1, 3, "alice", "bob"))
            : Json(UserPage(3, 3, "carol")));

        await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 2));

        var userQueries = handler.Requests
            .Where(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal))
            .Select(r => r.RequestUri!.Query)
            .ToList();

        Assert.That(userQueries[1], Does.Contain("startIndex=3"));
    }

    [Test]
    public async Task ImportAsync_EmptyFirstPage_FinishesWithoutAskingForMoreAsync()
    {
        using var handler = UserProvider(_ => Json("""{ "totalResults": 0, "Resources": [] }"""));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].PaginationTokens, Is.Empty);
        });
    }
    #endregion

    #region cursor paging
    [Test]
    public async Task ImportAsync_ProviderVolunteersACursor_SwitchesToCursorPagingAsync()
    {
        // Cursors are the more reliable walk: index paging over a set that changes mid-import can skip
        // or repeat resources.
        using var handler = UserProvider(query => query.Contains("cursor=next")
            ? Json("""{ "totalResults": 3, "Resources": [ { "id": "c", "userName": "carol" } ] }""")
            : Json("""
              { "totalResults": 3, "nextCursor": "next",
                "Resources": [ { "id": "a", "userName": "alice" }, { "id": "b", "userName": "bob" } ] }
              """));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 2));

        Assert.That(results.SelectMany(r => r.ImportObjects).ToList(), Has.Count.EqualTo(3));
        Assert.That(handler.Requests.Last().RequestUri!.Query, Does.Contain("cursor=next"));
    }

    [Test]
    public async Task ImportAsync_CursorModeSelected_AsksForCursorPagingFromTheFirstPageAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));

        await RunImportAsync(handler, ConnectedSystem(ScimConnectorConstants.PaginationModeCursor, selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        var firstUserQuery = handler.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).RequestUri!.Query;
        Assert.Multiple(() =>
        {
            Assert.That(firstUserQuery, Does.Contain("cursor="));
            Assert.That(firstUserQuery, Does.Not.Contain("startIndex"));
        });
    }

    [Test]
    public async Task ImportAsync_CursorPagingAndNoNextCursor_FinishesEvenOnAFullPageAsync()
    {
        // Under cursor paging the absence of a cursor ends the walk. Treating a full page as "more to
        // come" would loop for ever against a provider that always returns a full final page.
        using var handler = UserProvider(_ => Json(UserPage(1, 2, "alice", "bob")));

        var results = await RunImportAsync(handler, ConnectedSystem(ScimConnectorConstants.PaginationModeCursor, selectedObjectTypes: "User"), RunProfile(pageSize: 2));

        Assert.That(results, Has.Count.EqualTo(1));
    }
    #endregion

    #region resource types
    [Test]
    public async Task ImportAsync_SeveralSelectedObjectTypes_WalksEachInTurnAsync()
    {
        const string resourceTypes = """
        { "totalResults": 2, "Resources": [
            { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" },
            { "id": "Group", "name": "Group", "endpoint": "/Groups", "schema": "urn:ietf:params:scim:schemas:core:2.0:Group" } ] }
        """;
        using var handler = UserProvider(
            _ => Json("""{ "totalResults": 1, "Resources": [ { "id": "x", "displayName": "thing" } ] }"""),
            resourceTypes);

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: ["User", "Group"]), RunProfile(pageSize: 10));

        Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => o.ObjectType),
            Is.EquivalentTo(new[] { "Group", "User" }));
    }

    [Test]
    public async Task ImportAsync_ObjectTypeNotSelected_IsNotWalkedAsync()
    {
        const string resourceTypes = """
        { "totalResults": 2, "Resources": [
            { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" },
            { "id": "Group", "name": "Group", "endpoint": "/Groups", "schema": "urn:ietf:params:scim:schemas:core:2.0:Group" } ] }
        """;
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")), resourceTypes);

        await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.That(handler.Requests.Select(r => r.RequestUri!.AbsolutePath), Has.None.EndsWith("/Groups"));
    }

    [Test]
    public async Task ImportAsync_NoObjectTypeSelected_ImportsNothingWithoutAskingTheProviderForResourcesAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));
        var connectedSystem = ConnectedSystem(selectedObjectTypes: "User");
        connectedSystem.ObjectTypes!.ForEach(o => o.Selected = false);

        var results = await RunImportAsync(handler, connectedSystem, RunProfile(pageSize: 10));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].ImportObjects, Is.Empty);
            Assert.That(handler.Requests.Select(r => r.RequestUri!.AbsolutePath), Has.None.EndsWith("/Users"));
        });
    }

    [Test]
    public async Task ImportAsync_UsesTheEndpointTheProviderPublishedRatherThanAssumingUsersAsync()
    {
        // A provider is free to publish its resources somewhere else, and RFC 7643 says where.
        const string resourceTypes = """
        { "totalResults": 1, "Resources": [
            { "id": "User", "name": "User", "endpoint": "/v2/People", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" } ] }
        """;
        using var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
                return Json(resourceTypes);
            if (path.EndsWith("/People", StringComparison.Ordinal))
                return Json(UserPage(1, 1, "alice"));
            return NotFound();
        });

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.That(results.SelectMany(r => r.ImportObjects).ToList(), Has.Count.EqualTo(1));
    }
    #endregion

    #region run mechanics
    [Test]
    public async Task ImportAsync_DiscoversOnceForTheWholeRunRatherThanPerPageAsync()
    {
        using var handler = UserProvider(query => query.Contains("startIndex=1")
            ? Json(UserPage(1, 3, "alice", "bob"))
            : Json(UserPage(3, 3, "carol")));

        await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 2));

        Assert.That(handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/ResourceTypes", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    [Test]
    public async Task ImportAsync_ExcludedAttributes_AreAskedToBeLeftOutAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));

        await RunImportAsync(handler, ConnectedSystem(excludedAttributes: "photos, x509Certificates", selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        var userQuery = handler.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).RequestUri!.Query;
        Assert.That(userQuery, Does.Contain("excludedAttributes=photos%2Cx509Certificates"));
    }

    [Test]
    public async Task ImportAsync_AttributesTheAdministratorHasNotSelected_AreNotStagedAsync()
    {
        // Reading everything is deliberate (naming an inclusive set risks a provider returning nothing
        // else), but staging everything is not: an attribute deselected on purpose would still be
        // stored, which is data JIM was told not to keep.
        using var handler = UserProvider(_ => Json(
            "{ \"totalResults\": 1, \"Resources\": [ { \"id\": \"a\", \"userName\": \"alice\", \"nickName\": \"Ally\" } ] }"));

        var connectedSystem = ConnectedSystem(selectedObjectTypes: "User");
        connectedSystem.ObjectTypes!.Single().Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Name = "id", Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "userName", Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "nickName", Selected = false }
        ];

        var results = await RunImportAsync(handler, connectedSystem, RunProfile(pageSize: 10));

        Assert.That(results[0].ImportObjects[0].Attributes.Select(a => a.Name), Is.EquivalentTo(new[] { "id", "userName" }));
    }

    [Test]
    public async Task ImportAsync_ObjectTypeWithNoAttributeSelectionYet_StagesEverythingItCanReadAsync()
    {
        // Before a schema import there is nothing to filter by, and staging nothing would look like a
        // provider returning empty objects.
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));

        var results = await RunImportAsync(handler, ConnectedSystem(selectedObjectTypes: "User"), RunProfile(pageSize: 10));

        Assert.That(results[0].ImportObjects[0].Attributes.Select(a => a.Name), Does.Contain("userName"));
    }

    [Test]
    public void ImportAsync_WithoutOpeningTheConnection_Throws()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));
        var connector = new StubbedTransportScimConnector(handler);

        Assert.That(async () => await connector.ImportAsync(ConnectedSystem(), RunProfile(), [], null, _logger, CancellationToken.None, new RecordingConnectorProgress()),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ImportAsync_CancelledRun_StopsPromptly()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, 1, "alice")));
        var connector = new StubbedTransportScimConnector(handler);
        var connectedSystem = ConnectedSystem(selectedObjectTypes: "User");
        connector.OpenImportConnection(connectedSystem.SettingValues!, _logger);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await connector.ImportAsync(connectedSystem, RunProfile(), [], null, _logger, cancellation.Token, new RecordingConnectorProgress()),
            Throws.InstanceOf<OperationCanceledException>());

        connector.CloseImportConnection();
    }
    #endregion
}
