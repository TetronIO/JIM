// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.TestScimServiceProvider;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The connector driven end to end against <see cref="MockScimProvider"/>, covering the behaviour that
/// only shows up against a service provider with state: what a filter actually returns, what happens
/// when a cursor expires mid-walk, and whether a change made at the moment a run started reading
/// survives to the next one.
/// </summary>
[TestFixture]
public class ScimProviderScenarioTests
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
        string changeDetection = ScimConnectorConstants.ChangeDetectionAuto,
        params string[] selectedObjectTypes)
    {
        var objectTypes = (selectedObjectTypes.Length > 0 ? selectedObjectTypes : ["User"])
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
                Setting(ScimConnectorConstants.SettingChangeDetection, changeDetection)
            ]
        };
    }

    private static ConnectedSystemRunProfile RunProfile(ConnectedSystemRunType runType, int pageSize = 100)
    {
        return new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = pageSize };
    }

    private static List<string> ImportedIds(IEnumerable<ConnectedSystemImportResult> results)
    {
        return results
            .SelectMany(r => r.ImportObjects)
            .SelectMany(o => o.Attributes.Where(a => a.Name == "id").SelectMany(a => a.StringValues))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    #region delta import
    [Test]
    public async Task DeltaImport_AsksForAndReceivesOnlyTheResourcesThatChangedAsync()
    {
        var provider = new MockScimProvider();
        var watermark = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);
        provider.AddUser("alice", "alice", watermark.AddHours(-5));
        provider.AddUser("bob", "bob", watermark.AddHours(-5));
        provider.AddUser("carol", "carol", watermark.AddHours(1));

        using var handler = provider.CreateHandler();
        var persisted = new ScimImportState { Watermark = watermark }.Serialise();

        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.DeltaImport), _logger, persisted);

        Assert.That(ImportedIds(results), Is.EqualTo(new[] { "carol" }));
    }

    [Test]
    public async Task DeltaImport_ChangeMadeInTheSameSecondTheRunStartedReading_IsStillPickedUpNextRunAsync()
    {
        // The boundary the whole safety margin exists for. Resource metadata is published at one-second
        // precision, so a watermark set to the instant reading began would exclude this change for ever.
        var provider = new MockScimProvider();
        provider.Options.ProviderClock = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var alice = provider.AddUser("alice", "alice", provider.Options.ProviderClock.AddHours(-5));

        using var handler = provider.CreateHandler();
        var first = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);

        // Modified in the very second the first run asked its first question.
        alice.LastModified = provider.Options.ProviderClock;

        var second = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.DeltaImport), _logger, ScimImportRunner.PersistedConnectorData(first));

        Assert.That(ImportedIds(second), Is.EqualTo(new[] { "alice" }));
    }

    [Test]
    public async Task DeltaImport_NothingChangedSinceTheWatermark_ImportsNothingButStillMovesTheWatermarkOnAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice", provider.Options.ProviderClock.AddDays(-30));

        using var handler = provider.CreateHandler();
        var first = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);

        provider.Options.ProviderClock = provider.Options.ProviderClock.AddHours(1);

        var second = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.DeltaImport), _logger, ScimImportRunner.PersistedConnectorData(first));

        var advanced = ScimImportState.Read(ScimImportRunner.PersistedConnectorData(second), Log.Logger)?.Watermark;
        var original = ScimImportState.Read(ScimImportRunner.PersistedConnectorData(first), Log.Logger)?.Watermark;

        Assert.That(original, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(second.SelectMany(r => r.ImportObjects).ToList(), Is.Empty);
            // A quiet run is still evidence that nothing changed before it started, so the window narrows.
            Assert.That(advanced, Is.GreaterThan(original!.Value));
        });
    }

    [Test]
    public async Task DeltaImport_ProviderAdvertisesFilteringThenRejectsIt_ReadsEverythingAndSaysSoAsync()
    {
        // Advertising a capability and refusing to honour it is common enough that failing the run would
        // make the connector unusable against those providers.
        var provider = new MockScimProvider();
        provider.Options.RejectsFilters = true;
        provider.AddUser("alice", "alice");
        provider.AddUser("bob", "bob");

        using var handler = provider.CreateHandler();
        var persisted = new ScimImportState { Watermark = provider.Options.ProviderClock.AddDays(-1) }.Serialise();

        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.DeltaImport), _logger, persisted);

        Assert.Multiple(() =>
        {
            Assert.That(ImportedIds(results), Is.EqualTo(new[] { "alice", "bob" }));
            Assert.That(results[0].WarningMessage, Is.Not.Null);
            Assert.That(results[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
        });
    }

    [Test]
    public async Task DeltaImport_ProviderAcceptsTheFilterAndIgnoresIt_StillImportsEverythingItReturnedAsync()
    {
        // Nothing JIM can detect, but the objects must still be staged correctly rather than half read.
        var provider = new MockScimProvider();
        provider.Options.HonoursFiltering = false;
        provider.AddUser("alice", "alice", provider.Options.ProviderClock.AddDays(-30));
        provider.AddUser("bob", "bob", provider.Options.ProviderClock.AddDays(-30));

        using var handler = provider.CreateHandler();
        var persisted = new ScimImportState { Watermark = provider.Options.ProviderClock.AddDays(-1) }.Serialise();

        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.DeltaImport), _logger, persisted);

        Assert.That(ImportedIds(results), Is.EqualTo(new[] { "alice", "bob" }));
    }

    [Test]
    public async Task DeltaImport_GatewayClockRunningAheadOfTheResourceClock_DoesNotLoseAChangeAsync()
    {
        // The Date header need not come from the machine stamping meta.lastModified. A watermark taken
        // from the header without a margin would land in the resource clock's future.
        var provider = new MockScimProvider();
        provider.Options.ClockOffset = TimeSpan.FromSeconds(30);
        var alice = provider.AddUser("alice", "alice", provider.Options.ProviderClock.AddHours(-5));

        using var handler = provider.CreateHandler();
        var first = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);

        alice.LastModified = provider.Options.ProviderClock.AddSeconds(5);

        var second = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.DeltaImport), _logger, ScimImportRunner.PersistedConnectorData(first));

        Assert.That(ImportedIds(second), Is.EqualTo(new[] { "alice" }));
    }
    #endregion

    #region pagination
    [Test]
    public async Task Import_ProviderCapsThePageSizeBelowWhatWasAsked_ReadsEveryResourceAsync()
    {
        // Advancing by the requested count rather than by what came back would skip resources silently.
        var provider = new MockScimProvider();
        provider.Options.MaximumPageSize = 2;
        for (var index = 0; index < 5; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport, pageSize: 5), _logger);

        Assert.That(ImportedIds(results), Has.Count.EqualTo(5));
    }

    [Test]
    public async Task Import_CursorPagingProvider_WalksEveryPageAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.Pagination = MockScimPaginationStyle.Cursor;
        for (var index = 0; index < 5; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2), _logger);

        Assert.That(ImportedIds(results), Has.Count.EqualTo(5));
    }

    [Test]
    public void Import_CursorExpiresPartWayThroughTheWalk_FailsTheRunRatherThanTruncatingIt()
    {
        // A truncated run reads as a successful import of a fraction of the system, which deletion
        // detection would then act on. Failing loudly is the only safe answer.
        var provider = new MockScimProvider();
        provider.Options.Pagination = MockScimPaginationStyle.Cursor;
        for (var index = 0; index < 5; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();

        Assert.That(async () => await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
                RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2), _logger, afterPage: _ => provider.ExpireIssuedCursors()),
            Throws.TypeOf<ScimRequestException>());
    }
    #endregion

    #region paging deviations
    [Test]
    public async Task Import_ProviderOmittingTotalResults_StillReadsEveryPageAsync()
    {
        // RFC 7644 requires totalResults, but not every provider sends it. Without it the walk has only
        // the empty page to tell it it is done, which must still be enough.
        var provider = new MockScimProvider();
        provider.Options.OmitsTotalResults = true;
        for (var index = 0; index < 5; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2), _logger);

        Assert.That(ImportedIds(results), Has.Count.EqualTo(5));
    }

    [Test]
    public async Task Import_ProviderReportingThePageSizeAsTotalResults_StillReadsEveryResourceAsync()
    {
        // Reporting the page's size rather than the collection's is an easy provider mistake. Trusting
        // the total would stop the walk after one page and report the import a success.
        var provider = new MockScimProvider();
        provider.Options.ReportsPageSizeAsTotalResults = true;
        for (var index = 0; index < 5; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2), _logger);

        Assert.That(ImportedIds(results), Has.Count.EqualTo(5));
    }

    [Test]
    public async Task Import_SameResourceReturnedOnTwoPages_StagesBothRatherThanStoppingEarlyAsync()
    {
        // Index paging over a collection that gains a resource ahead of the current position shifts the
        // window, and the same resource arrives twice. The connector stages what it is given; JIM's
        // import processing reconciles on external id, so a duplicate is absorbed rather than lost.
        var provider = new MockScimProvider();
        provider.Options.RepeatsTheLastResourceOnEachPage = true;
        for (var index = 0; index < 4; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2), _logger);

        var imported = ImportedIds(results);
        Assert.Multiple(() =>
        {
            Assert.That(imported.Distinct().ToList(), Has.Count.EqualTo(4), "every resource was read");
            Assert.That(imported, Has.Count.GreaterThan(4), "and at least one was read twice");
        });
    }
    #endregion

    #region provider behaviour
    [Test]
    public async Task Import_ProviderPublishingNoSchemas_ReadsTheSameAttributesAsOneThatDoesAsync()
    {
        // The core-schema fallback is only worth having if it produces the same result.
        var withSchemas = new MockScimProvider();
        withSchemas.AddUser("alice", "alice");
        using var withSchemasHandler = withSchemas.CreateHandler();

        var withoutSchemas = new MockScimProvider();
        withoutSchemas.Options.PublishesSchemas = false;
        withoutSchemas.AddUser("alice", "alice");
        using var withoutSchemasHandler = withoutSchemas.CreateHandler();

        var published = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(withSchemasHandler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);
        var fallback = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(withoutSchemasHandler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);

        var publishedAttributes = published[0].ImportObjects[0].Attributes.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal);
        var fallbackAttributes = fallback[0].ImportObjects[0].Attributes.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.That(fallbackAttributes, Is.EqualTo(publishedAttributes));
    }

    [Test]
    public async Task Import_ThrottlingProvider_CompletesTheRunAfterBackingOffAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.ThrottleFirstCalls = 2;
        provider.AddUser("alice", "alice");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler, maximumRetries: 3), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);

        Assert.That(ImportedIds(results), Is.EqualTo(new[] { "alice" }));
    }

    [Test]
    public void Import_ProviderRejectingTheCredential_FailsTheRun()
    {
        var provider = new MockScimProvider();
        provider.Options.RequiredBearerToken = "a-different-token";
        provider.AddUser("alice", "alice");

        using var handler = provider.CreateHandler();

        Assert.That(async () => await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
                RunProfile(ConnectedSystemRunType.FullImport), _logger),
            Throws.TypeOf<ScimRequestException>());
    }

    [Test]
    public void Import_ProviderReturningAMalformedBody_FailsTheRun()
    {
        var provider = new MockScimProvider();
        provider.Options.ReturnsMalformedBody = true;
        provider.AddUser("alice", "alice");

        using var handler = provider.CreateHandler();

        Assert.That(async () => await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
                RunProfile(ConnectedSystemRunType.FullImport), _logger),
            Throws.TypeOf<ScimRequestException>());
    }

    [Test]
    public async Task Import_SeveralResourceTypes_ReadsEachFromTheEndpointTheProviderPublishedAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        provider.AddGroup("engineers", "Engineers");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler),
            ConnectedSystem(selectedObjectTypes: ["User", "Group"]), RunProfile(ConnectedSystemRunType.FullImport), _logger);

        Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => o.ObjectType).OrderBy(t => t, StringComparer.Ordinal),
            Is.EqualTo(new[] { "Group", "User" }));
    }

    [Test]
    public async Task Import_ProviderNamingListResponseMembersInLowerCase_ReadsThePageAnywayAsync()
    {
        // RFC 7643 section 2.1 makes attribute names case insensitive, so this is a conformant provider.
        // Matching member names by exact case would read every page as empty and import nothing.
        var provider = new MockScimProvider();
        provider.Options.UsesLowerCaseMemberNames = true;
        provider.AddUser("alice", "alice");

        using var handler = provider.CreateHandler();
        var results = await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
            RunProfile(ConnectedSystemRunType.FullImport), _logger);

        Assert.That(ImportedIds(results), Is.EqualTo(new[] { "alice" }));
    }

    [Test]
    public void Import_ServerErrorPartWayThroughTheWalk_FailsTheRunAndLeavesTheWatermarkAlone()
    {
        // The dangerous outcome is not the failure, it is a quiet end to the walk: a truncated import
        // reads as a successful one that found fewer objects, and deletion detection would act on that.
        var provider = new MockScimProvider();
        provider.Options.FailWithServerErrorOnRequest = 2;
        for (var index = 0; index < 5; index++)
            provider.AddUser($"user-{index}", $"user-{index}");

        using var handler = provider.CreateHandler();
        var connectedSystem = ConnectedSystem();
        var runProfile = RunProfile(ConnectedSystemRunType.FullImport, pageSize: 2);
        var connector = new StubbedTransportScimConnector(handler);
        var results = new List<ConnectedSystemImportResult>();
        connector.OpenImportConnection(connectedSystem.SettingValues!, _logger);

        try
        {
            Assert.That(async () =>
            {
                var tokens = new List<ConnectedSystemPaginationToken>();
                while (true)
                {
                    var result = await connector.ImportAsync(connectedSystem, runProfile, tokens, null, _logger, CancellationToken.None, new RecordingConnectorProgress());
                    results.Add(result);

                    if (result.PaginationTokens.Count == 0)
                        return;

                    tokens = result.PaginationTokens;
                }
            }, Throws.TypeOf<ScimRequestException>());
        }
        finally
        {
            connector.CloseImportConnection();
        }

        Assert.That(results.Select(r => r.PersistedConnectorData), Has.All.Null,
            "an abandoned run must not move the watermark on past resources it never read");
    }

    [Test]
    public void Import_ProviderReturningABareArrayInsteadOfAListResponse_FailsRatherThanImportingNothing()
    {
        // Reading it as an empty page would be the worst outcome: a successful import of no resources.
        var provider = new MockScimProvider();
        provider.Options.ReturnsBareArray = true;
        provider.AddUser("alice", "alice");

        using var handler = provider.CreateHandler();

        Assert.That(async () => await ScimImportRunner.RunAsync(new StubbedTransportScimConnector(handler), ConnectedSystem(),
                RunProfile(ConnectedSystemRunType.FullImport), _logger),
            Throws.TypeOf<ScimRequestException>());
    }

    [Test]
    public void ImportAsync_ProviderNotHonouringStartIndex_FailsRatherThanReadingForEver()
    {
        // Resuming at the ceiling proves the backstop without paging a hundred thousand times.
        var provider = new MockScimProvider();
        provider.AddUser("alice", "alice");
        provider.AddUser("bob", "bob");

        using var handler = provider.CreateHandler();
        var connectedSystem = ConnectedSystem();
        var connector = new StubbedTransportScimConnector(handler);
        connector.OpenImportConnection(connectedSystem.SettingValues!, _logger);

        var atTheCeiling = new ScimImportPosition { PagesRead = ScimConnectorImport.MaximumPagesPerResourceType - 1 };

        try
        {
            Assert.That(async () => await connector.ImportAsync(connectedSystem, RunProfile(ConnectedSystemRunType.FullImport, pageSize: 1),
                    [atTheCeiling.ToToken()], null, _logger, CancellationToken.None, new RecordingConnectorProgress()),
                Throws.TypeOf<InvalidOperationException>());
        }
        finally
        {
            connector.CloseImportConnection();
        }
    }
    #endregion
}
