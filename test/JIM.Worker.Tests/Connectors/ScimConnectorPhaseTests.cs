// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The conformance rules every phase-declaring Connector must satisfy, applied to the SCIM
/// Connector (#454).
/// </summary>
[TestFixture]
public class ScimConnectorPhaseConformanceTests : ConnectorPhaseConformanceTests
{
    protected override IConnectorPhases CreateConnector() => new ScimConnector();

    protected override ConnectedSystem CreateConnectedSystem() => ScimPhaseTestData.ConnectedSystem();
}

/// <summary>
/// What the SCIM Connector declares, and that a real import actually enters what it declared.
/// </summary>
/// <remarks>
/// A declaration nobody honours is worse than none: the stepper would show work that never starts.
/// The conformance suite above cannot catch that, because it never runs the Connector.
/// </remarks>
[TestFixture]
public class ScimConnectorPhaseDeclarationTests
{
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => (_logger as IDisposable)?.Dispose();

    private static ConnectedSystemRunProfile RunProfile(ConnectedSystemRunType runType) =>
        new() { Name = runType.ToString(), RunType = runType, PageSize = 2 };

    [Test]
    public void GetPhases_ForAFullImport_DeclaresDiscoveryThenFetch()
    {
        var phases = new ScimConnector().GetPhases(ScimPhaseTestData.ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport));

        Assert.That(phases.Select(p => p.Key),
            Is.EqualTo(new[] { ScimConnectorPhases.Discover, ScimConnectorPhases.Fetch }));
    }

    [Test]
    public void GetPhases_ForADeltaImport_DeclaresTheSameJourneyAsAFullImport()
    {
        // A delta is the same paged read with a filter on it, not an extra round trip, so inventing a
        // step for it would show an administrator work the Connector never does separately.
        var full = new ScimConnector().GetPhases(ScimPhaseTestData.ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport));
        var delta = new ScimConnector().GetPhases(ScimPhaseTestData.ConnectedSystem(), RunProfile(ConnectedSystemRunType.DeltaImport));

        Assert.That(delta.Select(p => p.Key), Is.EqualTo(full.Select(p => p.Key)));
    }

    [Test]
    public void GetPhases_ForAnExport_DeclaresNothing()
    {
        // Export is per object and JIM already reports accurate per-batch counts around the call, so a
        // step would say less than the counts already do. Same reasoning as the LDAP Connector.
        var phases = new ScimConnector().GetPhases(ScimPhaseTestData.ConnectedSystem(), RunProfile(ConnectedSystemRunType.Export));

        Assert.That(phases, Is.Empty);
    }

    [Test]
    public async Task ImportAsync_EntersEveryPhaseItDeclaredAsync()
    {
        var connectedSystem = ScimPhaseTestData.ConnectedSystem();
        var runProfile = RunProfile(ConnectedSystemRunType.FullImport);
        var progress = new RecordingConnectorProgress();

        await ScimImportRunner.RunAsync(
            new StubbedTransportScimConnector(ScimPhaseTestData.Provider()),
            connectedSystem, runProfile, _logger, progress: progress);

        var declared = new ScimConnector().GetPhases(connectedSystem, runProfile).Select(p => p.Key).ToList();

        Assert.That(progress.PhaseKeys.Distinct(), Is.EquivalentTo(declared));
    }

    [Test]
    public async Task ImportAsync_NarratesTheResourceTypeAndPageItIsFetchingAsync()
    {
        // The counts JIM keeps cannot move while a page is in flight, so this message is the only thing
        // distinguishing a healthy long fetch from a stuck one.
        var progress = new RecordingConnectorProgress();

        await ScimImportRunner.RunAsync(
            new StubbedTransportScimConnector(ScimPhaseTestData.Provider()),
            ScimPhaseTestData.ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport), _logger, progress: progress);

        Assert.That(progress.Messages, Has.Some.Contains("User").And.Some.Contains("page"));
    }
}

/// <summary>
/// A service provider and Connected System shared by the phase fixtures, kept in one place so the
/// conformance suite and the declaration tests interrogate the same configuration.
/// </summary>
internal static class ScimPhaseTestData
{
    internal static ConnectedSystem ConnectedSystem()
    {
        return new ConnectedSystem
        {
            Name = "SCIM",
            ObjectTypes = [new ConnectedSystemObjectType { Name = "User", Selected = true }],
            SettingValues =
            [
                new ConnectedSystemSettingValue
                {
                    Setting = new ConnectorDefinitionSetting { Name = ScimConnectorConstants.SettingBaseUrl },
                    StringValue = "https://provider.example.com/scim/v2"
                },
                new ConnectedSystemSettingValue
                {
                    Setting = new ConnectorDefinitionSetting { Name = ScimConnectorConstants.SettingPaginationMode },
                    StringValue = ScimConnectorConstants.PaginationModeAuto
                }
            ]
        };
    }

    /// <summary>
    /// A provider with a single page of Users, so an import completes in one page and the phases it
    /// entered are the whole journey.
    /// </summary>
    internal static StubHttpMessageHandler Provider()
    {
        return new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal))
                return Json("""{ "patch": { "supported": true }, "filter": { "supported": true } }""");

            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
            {
                return Json("""
                { "totalResults": 1, "Resources": [
                    { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" } ] }
                """);
            }

            if (path.EndsWith("/Schemas", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };

            if (path.EndsWith("/Users", StringComparison.Ordinal))
            {
                return Json("""
                { "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
                  "totalResults": 1, "startIndex": 1, "itemsPerPage": 1,
                  "Resources": [ { "id": "ada-id", "userName": "ada", "active": true } ] }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
        });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/scim+json") };
}
