// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Scim.Discovery;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Delta import: deciding how to detect change, filtering by the persisted watermark, and moving the
/// watermark on only once a run has read everything it was going to read.
/// </summary>
[TestFixture]
public class ScimDeltaImportTests
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

    private static ScimProviderCapabilities Capabilities(bool supportsFilter)
    {
        return ScimProviderCapabilities.From(new ScimServiceProviderConfig
        {
            Filter = new ScimFilterFeature { Supported = supportsFilter }
        });
    }

    private static readonly DateTimeOffset Watermark = new(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);

    #region strategy selection
    [Test]
    public void Create_FullImportRun_ScansEverythingWithoutAFilter()
    {
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.FullImport, ScimDeltaStrategy.Auto, Capabilities(true), Watermark);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.FullScan));
            Assert.That(plan.Filter, Is.Null);
            Assert.That(plan.WarningMessage, Is.Null);
        }
    }

    [Test]
    public void Create_DeltaRunWithAWatermarkAndAFilteringProvider_FiltersByLastModified()
    {
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.Auto, Capabilities(true), Watermark);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.LastModifiedFilter));
            Assert.That(plan.Filter, Is.EqualTo("meta.lastModified gt \"2026-07-30T10:00:00Z\""));
            Assert.That(plan.WarningMessage, Is.Null);
        }
    }

    [Test]
    public void Create_DeltaRunWithNoWatermarkYet_FallsBackToAFullScanAndSaysSo()
    {
        // The first delta after a system is configured has nothing to filter against. Falling back
        // establishes the watermark, where failing the run would leave it permanently unavailable.
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.Auto, Capabilities(true), watermark: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.FullScan));
            Assert.That(plan.Filter, Is.Null);
            Assert.That(plan.WarningMessage, Is.Not.Null);
            Assert.That(plan.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
        }
    }

    [Test]
    public void Create_DeltaRunAgainstAProviderThatCannotFilter_FallsBackToAFullScanAndSaysSo()
    {
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.Auto, Capabilities(false), Watermark);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.FullScan));
            Assert.That(plan.WarningMessage, Is.Not.Null);
            Assert.That(plan.WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
        }
    }

    [Test]
    public void Create_LastModifiedFilterForced_FiltersEvenWhenTheProviderDoesNotAdvertiseFiltering()
    {
        // The override exists because providers do support filtering without advertising it.
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.LastModifiedFilter, Capabilities(false), Watermark);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.LastModifiedFilter));
            Assert.That(plan.Filter, Is.Not.Null);
            Assert.That(plan.WarningMessage, Is.Null);
        }
    }

    [Test]
    public void Create_FullScanForced_ScansWithoutWarningBecauseThatIsWhatWasAskedFor()
    {
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.FullScan, Capabilities(true), Watermark);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.FullScan));
            Assert.That(plan.Filter, Is.Null);
            Assert.That(plan.WarningMessage, Is.Null);
        }
    }

    [Test]
    public void Create_ForcedLastModifiedFilterWithNoWatermark_StillFallsBackBecauseThereIsNothingToFilterAgainst()
    {
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.LastModifiedFilter, Capabilities(true), watermark: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Strategy, Is.EqualTo(ScimDeltaStrategy.FullScan));
            Assert.That(plan.WarningMessage, Is.Not.Null);
        }
    }

    [Test]
    public void Create_Filter_SendsTheWatermarkAsAQuotedUtcInstant()
    {
        // RFC 7644 section 3.4.2.2: the comparison value is a quoted string, and JIM stores UTC.
        var plan = ScimImportPlan.Create(ConnectedSystemRunType.DeltaImport, ScimDeltaStrategy.Auto, Capabilities(true),
            new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.FromHours(2)));

        Assert.That(plan.Filter, Is.EqualTo("meta.lastModified gt \"2026-07-30T10:00:00Z\""));
    }
    #endregion

    #region watermark tracking
    [Test]
    public void Resolve_NothingObserved_ReturnsNullSoTheStoredWatermarkIsLeftAlone()
    {
        Assert.That(new ScimWatermarkTracker().Resolve(), Is.Null);
    }

    [Test]
    public void Resolve_ProviderClockKnown_WatermarksBehindTheStartOfTheRun()
    {
        // Anything modified after the run started is re-read next time, so a change made to a page
        // already walked is not lost.
        var tracker = new ScimWatermarkTracker();
        tracker.ObserveProviderClock(Watermark);

        Assert.That(tracker.Resolve(), Is.EqualTo(Watermark - ScimWatermarkTracker.SafetyMargin));
    }

    [Test]
    public void ObserveProviderClock_SeveralPages_KeepsTheFirstBecauseThatIsWhenReadingBegan()
    {
        var tracker = new ScimWatermarkTracker();
        tracker.ObserveProviderClock(Watermark);
        tracker.ObserveProviderClock(Watermark.AddMinutes(5));

        Assert.That(tracker.Resolve(), Is.EqualTo(Watermark - ScimWatermarkTracker.SafetyMargin));
    }

    [Test]
    public void ObserveProviderClock_ProviderSendsNoDateHeader_FallsBackToTheHighestLastModifiedSeen()
    {
        var tracker = new ScimWatermarkTracker();
        tracker.ObserveProviderClock(null);
        tracker.ObserveLastModified(Watermark.AddMinutes(-10));
        tracker.ObserveLastModified(Watermark.AddMinutes(-2));

        Assert.That(tracker.Resolve(), Is.EqualTo(Watermark.AddMinutes(-2) - ScimWatermarkTracker.SafetyMargin));
    }

    [Test]
    public void Resolve_ProviderClockAvailable_IsPreferredOverTheHighestLastModifiedSeen()
    {
        // The highest observed value never advances past the newest object, so a directory that stops
        // changing would keep re-importing itself for ever if the watermark came from the data.
        var tracker = new ScimWatermarkTracker();
        tracker.ObserveProviderClock(Watermark);
        tracker.ObserveLastModified(Watermark.AddYears(-1));

        Assert.That(tracker.Resolve(), Is.EqualTo(Watermark - ScimWatermarkTracker.SafetyMargin));
    }
    #endregion

    #region persisted state
    [Test]
    public void Read_NoPersistedData_ReturnsNull()
    {
        Assert.That(ScimImportState.Read(null, Serilog.Log.Logger), Is.Null);
    }

    [Test]
    public void Read_UnreadablePersistedData_ReturnsNullRatherThanFailingTheRun()
    {
        // A full scan re-establishes the watermark; failing would leave the system stuck.
        Assert.That(ScimImportState.Read("not json", Serilog.Log.Logger), Is.Null);
    }

    [Test]
    public void Read_RoundTripsTheWatermark()
    {
        var serialised = new ScimImportState { Watermark = Watermark, CapturedAt = Watermark }.Serialise();

        Assert.That(ScimImportState.Read(serialised, Serilog.Log.Logger)?.Watermark, Is.EqualTo(Watermark));
    }
    #endregion

    #region end to end
    private static ConnectedSystemSettingValue Setting(string name, string? stringValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue
        };
    }

    private static ConnectedSystem ConnectedSystem(string changeDetection = ScimConnectorConstants.ChangeDetectionAuto)
    {
        return new ConnectedSystem
        {
            Name = "SCIM",
            ObjectTypes = [new ConnectedSystemObjectType { Name = "User", Selected = true }],
            SettingValues =
            [
                Setting(ScimConnectorConstants.SettingBaseUrl, "https://provider.example.com/scim/v2"),
                Setting(ScimConnectorConstants.SettingPaginationMode, ScimConnectorConstants.PaginationModeAuto),
                Setting(ScimConnectorConstants.SettingChangeDetection, changeDetection)
            ]
        };
    }

    private static ConnectedSystemRunProfile RunProfile(ConnectedSystemRunType runType = ConnectedSystemRunType.DeltaImport, int pageSize = 10)
    {
        return new ConnectedSystemRunProfile { Name = "Delta Import", RunType = runType, PageSize = pageSize };
    }

    private static HttpResponseMessage Json(string body, DateTimeOffset? serverDate = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };

        if (serverDate.HasValue)
            response.Headers.Date = serverDate;

        return response;
    }

    private static HttpResponseMessage NotFound()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
    }

    /// <summary>
    /// A provider publishing only the User resource type, whose /Users pages come from the supplied
    /// responder keyed by the page's query string.
    /// </summary>
    private static StubHttpMessageHandler UserProvider(Func<string, HttpResponseMessage> usersResponder, bool supportsFilter = true)
    {
        return new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal))
                return Json($$"""{ "filter": { "supported": {{(supportsFilter ? "true" : "false")}} } }""");

            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
            {
                return Json("""
                { "totalResults": 1, "Resources": [
                    { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" } ] }
                """);
            }

            if (path.EndsWith("/Users", StringComparison.Ordinal))
                return usersResponder(request.RequestUri.Query);

            return NotFound();
        });
    }

    private Task<List<ConnectedSystemImportResult>> RunImportAsync(
        StubHttpMessageHandler handler,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        string? persistedConnectorData)
    {
        return ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), connectedSystem, runProfile, _logger, persistedConnectorData);
    }

    /// <param name="totalResults">
    /// What the provider says it holds overall, which is how the connector knows whether to ask for
    /// another page. It is not the size of this page.
    /// </param>
    private static string UserPage(int totalResults, params string[] ids)
    {
        var resources = string.Join(",", ids.Select(id =>
            $$"""{ "id": "{{id}}", "userName": "{{id}}", "meta": { "lastModified": "2026-07-30T09:00:00Z" } }"""));

        return $$"""{ "totalResults": {{totalResults}}, "Resources": [ {{resources}} ] }""";
    }

    [Test]
    public async Task ImportAsync_DeltaRunWithAWatermark_AsksTheProviderOnlyForWhatChangedAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, "alice")));
        var persisted = new ScimImportState { Watermark = Watermark }.Serialise();

        await RunImportAsync(handler, ConnectedSystem(), RunProfile(), persisted);

        var userQuery = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).RequestUri!.Query;
        Assert.That(Uri.UnescapeDataString(userQuery), Does.Contain("filter=meta.lastModified gt \"2026-07-30T10:00:00Z\""));
    }

    [Test]
    public async Task ImportAsync_FullImportRun_DoesNotFilterAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, "alice")));
        var persisted = new ScimImportState { Watermark = Watermark }.Serialise();

        await RunImportAsync(handler, ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport), persisted);

        var userQuery = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).RequestUri!.Query;
        Assert.That(userQuery, Does.Not.Contain("filter"));
    }

    [Test]
    public async Task ImportAsync_DeltaRunWithoutAWatermark_ScansEverythingAndWarnsAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, "alice")));

        var results = await RunImportAsync(handler, ConnectedSystem(), RunProfile(), persistedConnectorData: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].WarningMessage, Is.Not.Null);
            Assert.That(results[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(results[0].ImportObjects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ImportAsync_FinalPage_PersistsAWatermarkTakenFromTheProviderClockAsync()
    {
        var providerClock = new DateTimeOffset(2026, 7, 30, 11, 0, 0, TimeSpan.Zero);
        using var handler = UserProvider(_ => Json(UserPage(1, "alice"), providerClock));

        var results = await RunImportAsync(handler, ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport), null);
        var state = ScimImportState.Read(results.Single().PersistedConnectorData, Serilog.Log.Logger);

        Assert.That(state?.Watermark, Is.EqualTo(providerClock - ScimWatermarkTracker.SafetyMargin));
    }

    [Test]
    public async Task ImportAsync_PagesBeforeTheLast_PersistNothingSoAnAbandonedRunDoesNotMoveTheWatermarkAsync()
    {
        var providerClock = new DateTimeOffset(2026, 7, 30, 11, 0, 0, TimeSpan.Zero);
        using var handler = UserProvider(query => query.Contains("startIndex=1")
            ? Json(UserPage(3, "alice", "bob"), providerClock)
            : Json(UserPage(3, "carol"), providerClock));

        var results = await RunImportAsync(handler, ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].PersistedConnectorData, Is.Null);
            Assert.That(results[1].PersistedConnectorData, Is.Not.Null);
        }
    }

    [Test]
    public async Task ImportAsync_ProviderSendsNoDateHeader_WatermarksFromTheNewestResourceSeenAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, "alice")));

        var results = await RunImportAsync(handler, ConnectedSystem(), RunProfile(ConnectedSystemRunType.FullImport), null);
        var state = ScimImportState.Read(results.Single().PersistedConnectorData, Serilog.Log.Logger);

        Assert.That(state?.Watermark, Is.EqualTo(new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero) - ScimWatermarkTracker.SafetyMargin));
    }

    [Test]
    public async Task ImportAsync_ProviderThatCannotFilter_ScansEverythingAndWarnsAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, "alice")), supportsFilter: false);
        var persisted = new ScimImportState { Watermark = Watermark }.Serialise();

        var results = await RunImportAsync(handler, ConnectedSystem(), RunProfile(), persisted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].WarningMessage, Is.Not.Null);
            Assert.That(handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).RequestUri!.Query,
                Does.Not.Contain("filter"));
        }
    }

    [Test]
    public async Task ImportAsync_FullScanForcedBySetting_DoesNotFilterAsync()
    {
        using var handler = UserProvider(_ => Json(UserPage(1, "alice")));
        var persisted = new ScimImportState { Watermark = Watermark }.Serialise();

        var results = await RunImportAsync(handler, ConnectedSystem(ScimConnectorConstants.ChangeDetectionFullScan), RunProfile(), persisted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)).RequestUri!.Query,
                Does.Not.Contain("filter"));
            // Deliberate configuration, so it is not reported as a shortfall on every run.
            Assert.That(results[0].WarningMessage, Is.Null);
        }
    }
    #endregion

    #region excluded attributes
    [Test]
    public void ParseExcludedAttributes_AttributesImportDependsOn_AreNeverExcluded()
    {
        // Excluding these would leave resources with no identifier to anchor on and no timestamp to
        // watermark against, which would break delta import silently.
        var excluded = ScimQueryBuilder.ParseExcludedAttributes("photos, id, meta, x509Certificates");

        Assert.That(excluded, Is.EqualTo(new[] { "photos", "x509Certificates" }));
    }
    #endregion
}
