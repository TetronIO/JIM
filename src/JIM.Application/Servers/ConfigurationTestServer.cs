// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers.ConfigurationTests;
using Stopwatch = System.Diagnostics.Stopwatch;
using JIM.Application.Utilities;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Saved configuration test cases (#2055 Phase 4): create, update and delete them, each recorded in the Configuration
/// History of what owns the case, and run them. Every surface (portal, REST API, PowerShell) calls this, so each checks
/// a case the same way. A case's inputs and results are never logged: inputs loaded from a real object can be personal
/// data. Activities name the case, never its values.
/// </summary>
public class ConfigurationTestServer
{
    private JimApplication Application { get; }

    private readonly ConfigurationTestRunner _runner;
    private readonly ConfigurationTestGate _gate;

    internal ConfigurationTestServer(JimApplication application)
    {
        Application = application;
        _runner = new ConfigurationTestRunner(new DynamicExpressoEvaluator());
        _gate = new ConfigurationTestGate(_runner);
    }

    /// <summary>
    /// The clock the save gate reads once per save, so both its runs are as of the same instant. Replaceable in tests.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    #region Read

    public async Task<ConfigurationTestCase?> GetTestCaseAsync(int id) =>
        await Application.Repository.ConfigurationTests.GetTestCaseAsync(id);

    /// <summary>
    /// The cases covering a Synchronisation Rule, those under a Connected System, or every case when neither is given.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesAsync(int? syncRuleId = null, int? connectedSystemId = null)
    {
        if (syncRuleId.HasValue && connectedSystemId.HasValue)
            throw new ArgumentException("Give a Synchronisation Rule or a Connected System, not both.");

        if (syncRuleId.HasValue)
            return await Application.Repository.ConfigurationTests.GetTestCasesForSyncRuleAsync(syncRuleId.Value);
        if (connectedSystemId.HasValue)
            return await Application.Repository.ConfigurationTests.GetTestCasesForConnectedSystemAsync(connectedSystemId.Value);
        return await Application.Repository.ConfigurationTests.GetAllTestCasesAsync();
    }

    /// <summary>
    /// The cases covering a Connected System's Simple mode Object Matching Rules: those its Object Matching tab shows.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationTestCase>> GetSimpleModeMatchingTestCasesAsync(int connectedSystemId) =>
        await Application.Repository.ConfigurationTests.GetSimpleModeMatchingTestCasesAsync(connectedSystemId);

    #endregion

    #region Create, update, delete

    public Task CreateTestCaseAsync(ConfigurationTestCase testCase, MetaverseObject? initiatedBy) =>
        CreateTestCaseAsync(testCase, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            () => AuditHelper.SetCreated(testCase, initiatedBy));

    public Task CreateTestCaseAsync(ConfigurationTestCase testCase, ApiKey initiatedByApiKey) =>
        CreateTestCaseAsync(testCase, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            () => AuditHelper.SetCreated(testCase, initiatedByApiKey));

    public Task UpdateTestCaseAsync(ConfigurationTestCase testCase, MetaverseObject? initiatedBy) =>
        UpdateTestCaseAsync(testCase, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            () => AuditHelper.SetUpdated(testCase, initiatedBy));

    public Task UpdateTestCaseAsync(ConfigurationTestCase testCase, ApiKey initiatedByApiKey) =>
        UpdateTestCaseAsync(testCase, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            () => AuditHelper.SetUpdated(testCase, initiatedByApiKey));

    public Task DeleteTestCaseAsync(ConfigurationTestCase testCase, MetaverseObject? initiatedBy) =>
        DeleteTestCaseAsync(testCase, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));

    public Task DeleteTestCaseAsync(ConfigurationTestCase testCase, ApiKey initiatedByApiKey) =>
        DeleteTestCaseAsync(testCase, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));

    private async Task CreateTestCaseAsync(ConfigurationTestCase testCase, Func<Activity, Task> createActivity, Action setAudit)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var owner = await EnsureWorkableAsync(testCase);
        await EnsureOwnerHasRoomAsync(owner);

        var activity = NewActivity(testCase, owner, ActivityTargetOperationType.Create);
        await createActivity(activity);
        setAudit();
        await Application.Repository.ConfigurationTests.CreateTestCaseAsync(testCase);
        await CaptureAsync(activity, owner);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    private async Task UpdateTestCaseAsync(ConfigurationTestCase testCase, Func<Activity, Task> createActivity, Action setAudit)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var stored = await Application.Repository.ConfigurationTests.GetTestCaseAsync(testCase.Id)
            ?? throw new ArgumentException($"There is no configuration test case {testCase.Id}.");
        if (stored.Kind != testCase.Kind || stored.SyncRuleMappingId != testCase.SyncRuleMappingId ||
            stored.SyncRuleId != testCase.SyncRuleId || stored.ObjectMatchingRuleId != testCase.ObjectMatchingRuleId)
            throw new ArgumentException("A test case keeps what it covers and what it tests; to test something else, create another test case.");

        var owner = await EnsureWorkableAsync(testCase);
        testCase.Created = stored.Created;
        testCase.CreatedByType = stored.CreatedByType;
        testCase.CreatedById = stored.CreatedById;
        testCase.CreatedByName = stored.CreatedByName;

        var activity = NewActivity(testCase, owner, ActivityTargetOperationType.Update);
        await createActivity(activity);
        setAudit();
        await Application.Repository.ConfigurationTests.UpdateTestCaseAsync(testCase);
        await CaptureAsync(activity, owner);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    private async Task DeleteTestCaseAsync(ConfigurationTestCase testCase, Func<Activity, Task> createActivity)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var owner = await ResolveOwnerAsync(testCase)
            ?? throw new ArgumentException($"Configuration test case {testCase.Id} covers nothing that exists.");

        var activity = NewActivity(testCase, owner, ActivityTargetOperationType.Delete);
        await createActivity(activity);
        await Application.Repository.ConfigurationTests.DeleteTestCaseAsync(testCase.Id);
        await CaptureAsync(activity, owner);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Refuses a case that cannot be saved: one whose shape is wrong (<see cref="ConfigurationTestCase.DescribeInvalidity"/>),
    /// that covers something that does not exist, or whose kind does not fit what it covers. Returns what owns it.
    /// </summary>
    private async Task<Owner> EnsureWorkableAsync(ConfigurationTestCase testCase)
    {
        if (testCase.DescribeInvalidity() is { } invalid)
            throw new ArgumentException(invalid);

        if (testCase.SyncRuleMappingId is { } mappingId)
        {
            var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId)
                ?? throw new ArgumentException($"There is no Attribute Flow {mappingId} for the test case to cover.");
            if (testCase.Kind == ConfigurationTestCaseKind.GeneratedValue && mapping.Generation == null)
                throw new ArgumentException("A Generated Value test case covers an Attribute Flow that generates a value; this one does not, so test it as an Attribute Flow.");
            if (testCase.Kind == ConfigurationTestCaseKind.AttributeFlow && mapping.Generation != null)
                throw new ArgumentException("This Attribute Flow generates a value, so its base is what to test; test it as a Generated Value.");
        }

        return await ResolveOwnerAsync(testCase)
            ?? throw new ArgumentException(testCase.Kind switch
            {
                ConfigurationTestCaseKind.Scope => $"There is no Synchronisation Rule {testCase.SyncRuleId} for the test case to cover.",
                ConfigurationTestCaseKind.ObjectMatchingRule => $"There is no Object Matching Rule {testCase.ObjectMatchingRuleId} for the test case to cover.",
                _ => $"There is no Attribute Flow {testCase.SyncRuleMappingId} for the test case to cover."
            });
    }

    /// <summary>
    /// Refuses a new case when what owns it already holds <see cref="ConfigurationTestCase.MaxTestCasesPerOwner"/>: the
    /// save gate runs every case covering what a save changes, twice.
    /// </summary>
    private async Task EnsureOwnerHasRoomAsync(Owner owner)
    {
        var held = owner.TargetType == ActivityTargetType.SynchronisationRule
            ? (await Application.Repository.ConfigurationTests.GetTestCasesForSyncRuleAsync(owner.Id)).Count
            : (await Application.Repository.ConfigurationTests.GetSimpleModeMatchingTestCasesAsync(owner.Id)).Count;
        if (held >= ConfigurationTestCase.MaxTestCasesPerOwner)
            throw new ArgumentException(
                $"'{owner.Name}' already has {held} configuration test cases, the most one {(owner.TargetType == ActivityTargetType.SynchronisationRule ? "Synchronisation Rule" : "Connected System")} " +
                $"can hold ({ConfigurationTestCase.MaxTestCasesPerOwner}). Delete a case it no longer needs first.");
    }

    private static Activity NewActivity(ConfigurationTestCase testCase, Owner owner, ActivityTargetOperationType operation)
    {
        var activity = new Activity
        {
            // The case's name only: its inputs and expectation are never written to an Activity.
            TargetName = $"{Activity.ConfigurationTestCaseTargetNamePrefix}'{testCase.Name}'",
            TargetContext = owner.Name,
            TargetType = owner.TargetType,
            TargetOperationType = operation
        };
        activity.SetConfigurationTargetId(owner.TargetType, owner.Id);
        return activity;
    }

    private Task CaptureAsync(Activity activity, Owner owner) => owner.TargetType == ActivityTargetType.SynchronisationRule
        ? Application.ConnectedSystems.CaptureSyncRuleConfigurationChangeAsync(activity, owner.Id)
        : Application.ConnectedSystems.CaptureConnectedSystemConfigurationChangeAsync(activity, owner.Id);

    /// <summary>
    /// What owns a case, and so whose Configuration History records it: the Synchronisation Rule holding what it
    /// covers, or for a Simple mode Object Matching Rule, the Connected System. Null when what it covers is gone.
    /// </summary>
    private async Task<Owner?> ResolveOwnerAsync(ConfigurationTestCase testCase)
    {
        int? syncRuleId = testCase.SyncRuleId;
        if (testCase.SyncRuleMappingId is { } mappingId)
            syncRuleId = (await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId))?.SyncRuleId;

        if (testCase.ObjectMatchingRuleId is { } matchingRuleId)
        {
            var matchingRule = await Application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(matchingRuleId);
            if (matchingRule == null)
                return null;
            syncRuleId = matchingRule.SyncRuleId;
            if (syncRuleId == null && matchingRule.ConnectedSystemObjectTypeId is { } objectTypeId)
            {
                var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
                return objectType?.ConnectedSystem == null
                    ? null
                    : new Owner(ActivityTargetType.ConnectedSystem, objectType.ConnectedSystem.Id, objectType.ConnectedSystem.Name);
            }
        }

        if (syncRuleId is not { } ruleId)
            return null;
        var rule = await Application.Repository.ConnectedSystems.GetSyncRuleAsync(ruleId);
        return rule == null ? null : new Owner(ActivityTargetType.SynchronisationRule, rule.Id, rule.Name);
    }

    private sealed record Owner(ActivityTargetType TargetType, int Id, string Name);

    #endregion

    #region Save gate

    /// <summary>
    /// The save gate for a save of a Synchronisation Rule, or of something it holds (#2055 Phase 4b): runs every case
    /// covering the rule against the rule as stored and as <paramref name="proposed"/>, as of one instant. Call it
    /// before anything in the save flushes, so the stored rule is still the "before".
    /// </summary>
    /// <param name="proposed">The rule as the save would leave it, with its Attribute Flows, Scope and own Object
    /// Matching Rules, and both object types' attributes.</param>
    /// <returns>What the save must apply and record: cases whose new result is accepted, and cases saved past.</returns>
    /// <exception cref="ConfigurationTestsRegressedException">The save stops a case passing and
    /// <paramref name="options"/> neither accepts the new result nor saves anyway. Nothing has been saved.</exception>
    internal async Task<ConfigurationTestGateOutcome> GateSyncRuleSaveAsync(SyncRule proposed, ConfigurationTestGateOptions? options)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (proposed.Id <= 0)
            return ConfigurationTestGateOutcome.Nothing;

        var testCases = await Application.Repository.ConfigurationTests.GetTestCasesForSyncRuleAsync(proposed.Id);
        if (testCases.Count == 0)
            return ConfigurationTestGateOutcome.Nothing;

        var stored = await Application.Repository.ConnectedSystems.GetSyncRuleUntrackedAsync(proposed.Id);
        return stored == null
            ? ConfigurationTestGateOutcome.Nothing
            : Decide(testCases, ConfigurationTestSubject.Of(stored), ConfigurationTestSubject.Of(proposed), options,
                "Synchronisation Rule", proposed.Id);
    }

    /// <summary>
    /// The save gate for a save of one part of a Synchronisation Rule, given as a change to the rule as stored: an
    /// Attribute Flow's settings, or one of its Object Matching Rules.
    /// </summary>
    /// <param name="syncRuleId">The rule the part belongs to.</param>
    /// <param name="propose">Applies the save's change to a copy of the stored rule.</param>
    internal async Task<ConfigurationTestGateOutcome> GateSyncRulePartSaveAsync(int syncRuleId, Action<SyncRule> propose,
        ConfigurationTestGateOptions? options)
    {
        ArgumentNullException.ThrowIfNull(propose);
        var testCases = await Application.Repository.ConfigurationTests.GetTestCasesForSyncRuleAsync(syncRuleId);
        if (testCases.Count == 0)
            return ConfigurationTestGateOutcome.Nothing;

        // Two reads, so the change lands on a copy and the stored one stays as it is.
        var stored = await Application.Repository.ConnectedSystems.GetSyncRuleUntrackedAsync(syncRuleId);
        var proposed = await Application.Repository.ConnectedSystems.GetSyncRuleUntrackedAsync(syncRuleId);
        if (stored == null || proposed == null)
            return ConfigurationTestGateOutcome.Nothing;

        propose(proposed);
        return Decide(testCases, ConfigurationTestSubject.Of(stored), ConfigurationTestSubject.Of(proposed), options,
            "Synchronisation Rule", syncRuleId);
    }

    /// <summary>
    /// The save gate for a save of a Connected System's Simple mode Object Matching Rule.
    /// </summary>
    /// <param name="proposed">The rule as the save would leave it.</param>
    internal async Task<ConfigurationTestGateOutcome> GateSimpleModeMatchingRuleSaveAsync(ObjectMatchingRule proposed,
        ConfigurationTestGateOptions? options)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (proposed.Id <= 0 || proposed.ConnectedSystemObjectTypeId is not { } objectTypeId)
            return ConfigurationTestGateOutcome.Nothing;

        var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null)
            return ConfigurationTestGateOutcome.Nothing;

        var testCases = (await Application.Repository.ConfigurationTests.GetSimpleModeMatchingTestCasesAsync(objectType.ConnectedSystemId))
            .Where(c => c.ObjectMatchingRuleId == proposed.Id)
            .ToList();
        if (testCases.Count == 0)
            return ConfigurationTestGateOutcome.Nothing;

        var stored = await Application.Repository.ConnectedSystems.GetObjectMatchingRuleUntrackedAsync(proposed.Id);
        return stored == null
            ? ConfigurationTestGateOutcome.Nothing
            : Decide(testCases, ConfigurationTestSubject.Of(stored, objectType), ConfigurationTestSubject.Of(proposed, objectType), options,
                "Object Matching Rule", proposed.Id);
    }

    /// <summary>
    /// Rewrites each case whose new result the save accepted, so it expects that result. Call after the save's own
    /// write and before its change is captured, so the cases' changes are in the same Configuration History version.
    /// </summary>
    internal async Task ApplyAcceptedResultsAsync(ConfigurationTestGateOutcome outcome, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        foreach (var regression in outcome.Accepted)
        {
            var testCase = regression.TestCase;
            testCase.Expectation = regression.AcceptedExpectation!;
            if (initiatedByApiKey != null)
                AuditHelper.SetUpdated(testCase, initiatedByApiKey);
            else
                AuditHelper.SetUpdated(testCase, initiatedBy);
            await Application.Repository.ConfigurationTests.UpdateTestCaseAsync(testCase);
        }
    }

    private ConfigurationTestGateOutcome Decide(IReadOnlyList<ConfigurationTestCase> testCases, ConfigurationTestSubject stored,
        ConfigurationTestSubject proposed, ConfigurationTestGateOptions? options, string ownerKind, int ownerId)
    {
        // One instant for both runs: a case reading the clock must not regress between them.
        var nowUtc = UtcNow();
        var stopwatch = Stopwatch.StartNew();
        var regressions = _gate.FindRegressions(testCases, stored, proposed, nowUtc);
        stopwatch.Stop();
        Log.Debug("ConfigurationTestServer: save gate for {OwnerKind} {OwnerId} ran {Count} configuration test case(s) against the " +
                  "stored and proposed configuration in {ElapsedMs} ms; {Regressions} stopped passing",
            ownerKind, ownerId, testCases.Count, stopwatch.ElapsedMilliseconds, regressions.Count);
        if (regressions.Count == 0)
            return ConfigurationTestGateOutcome.Nothing;

        options ??= ConfigurationTestGateOptions.None;
        var accepted = options.AcceptNewResults ? regressions.Where(r => r.CanAccept).ToList() : [];
        var savedPast = regressions.Except(accepted).ToList();
        if (savedPast.Count > 0 && !options.SaveAnyway)
        {
            Log.Information("ConfigurationTestServer: refused a save of {OwnerKind} {OwnerId}: it stops {Count} configuration test case(s) passing",
                ownerKind, ownerId, regressions.Count);
            throw new ConfigurationTestsRegressedException(regressions);
        }

        Log.Information("ConfigurationTestServer: a save of {OwnerKind} {OwnerId} stops {Count} configuration test case(s) passing; " +
                        "{Accepted} accepted as expected, {SavedPast} saved past",
            ownerKind, ownerId, regressions.Count, accepted.Count, savedPast.Count);
        return new ConfigurationTestGateOutcome { Accepted = accepted, SavedPast = savedPast };
    }

    #endregion

    #region Run

    /// <summary>
    /// Runs every case covering a Synchronisation Rule, in id order.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationTestRunResult>> RunForSyncRuleAsync(int syncRuleId) =>
        await RunAsync(await Application.Repository.ConfigurationTests.GetTestCasesForSyncRuleAsync(syncRuleId));

    /// <summary>
    /// Runs every case under a Connected System, in id order: its Synchronisation Rules' and its Simple mode Object
    /// Matching Rules'.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationTestRunResult>> RunForConnectedSystemAsync(int connectedSystemId) =>
        await RunAsync(await Application.Repository.ConfigurationTests.GetTestCasesForConnectedSystemAsync(connectedSystemId));

    /// <summary>
    /// Runs the cases with these ids, in id order; an id with no case is left out.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationTestRunResult>> RunByIdsAsync(IReadOnlyCollection<int> ids) =>
        await RunAsync(await Application.Repository.ConfigurationTests.GetTestCasesAsync(ids));

    /// <summary>
    /// Runs <paramref name="testCases"/> against the configuration as saved, each as of its own run-as-of time or now.
    /// Each Synchronisation Rule and Object Type is read once. A case that cannot be run at all is an Error result; the
    /// others still run.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationTestRunResult>> RunAsync(IReadOnlyList<ConfigurationTestCase> testCases)
    {
        ArgumentNullException.ThrowIfNull(testCases);

        var nowUtc = DateTime.UtcNow;
        var syncRules = new Dictionary<int, SyncRule?>();
        var objectTypes = new Dictionary<int, ConnectedSystemObjectType?>();
        var results = new List<ConfigurationTestRunResult>(testCases.Count);
        foreach (var testCase in testCases)
        {
            try
            {
                results.Add(await RunOneAsync(testCase, nowUtc, syncRules, objectTypes));
            }
            catch (Exception ex)
            {
                // The exception's type only: its message can quote the case's input values.
                Log.Warning("ConfigurationTestServer: configuration test case {TestCaseId} could not be run ({ExceptionType})",
                    testCase.Id, ex.GetType().Name);
                results.Add(ConfigurationTestRunner.CouldNotRun(testCase, $"{ex.GetType().Name}.", nowUtc));
            }
        }

        Log.Information("ConfigurationTestServer: ran {Count} configuration test case(s): {Passed} passed, {Failed} failed, " +
                        "{NeedsReview} need review, {Errors} could not run",
            results.Count,
            results.Count(r => r.Status == ConfigurationTestStatus.Passed),
            results.Count(r => r.Status == ConfigurationTestStatus.Failed),
            results.Count(r => r.Status == ConfigurationTestStatus.NeedsReview),
            results.Count(r => r.Status == ConfigurationTestStatus.Error));
        return results;
    }

    private async Task<ConfigurationTestRunResult> RunOneAsync(ConfigurationTestCase testCase, DateTime nowUtc,
        Dictionary<int, SyncRule?> syncRules, Dictionary<int, ConnectedSystemObjectType?> objectTypes)
    {
        switch (testCase.Kind)
        {
            case ConfigurationTestCaseKind.AttributeFlow or ConfigurationTestCaseKind.GeneratedValue:
            {
                var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(testCase.SyncRuleMappingId!.Value);
                var rule = mapping == null ? null : await SyncRuleAsync(mapping.SyncRuleId, syncRules);
                return rule == null
                    ? ConfigurationTestRunner.CouldNotRun(testCase, "its Attribute Flow no longer exists.", nowUtc)
                    : _runner.RunAttributeFlow(testCase, rule, nowUtc);
            }

            case ConfigurationTestCaseKind.Scope:
            {
                var rule = await SyncRuleAsync(testCase.SyncRuleId!.Value, syncRules);
                return rule == null
                    ? ConfigurationTestRunner.CouldNotRun(testCase, "its Synchronisation Rule no longer exists.", nowUtc)
                    : _runner.RunScope(testCase, rule, nowUtc);
            }

            default:
            {
                var stored = await Application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(testCase.ObjectMatchingRuleId!.Value);
                if (stored == null)
                    return ConfigurationTestRunner.CouldNotRun(testCase, "its Object Matching Rule no longer exists.", nowUtc);

                // In Synchronisation Rule mode the rule reads its Synchronisation Rule's Object Type. Its match values
                // never need the Metaverse Object Type (only a lookup does), and giving a Synchronisation Rule mode rule
                // one makes it invalid, so the rule runs as it is stored.
                var objectType = stored.SyncRuleId is { } ruleId
                    ? (await SyncRuleAsync(ruleId, syncRules))?.ConnectedSystemObjectType
                    : await ObjectTypeAsync(stored.ConnectedSystemObjectTypeId, objectTypes);

                return objectType == null
                    ? ConfigurationTestRunner.CouldNotRun(testCase, "the Object Type its Object Matching Rule reads no longer exists.", nowUtc)
                    : _runner.RunObjectMatching(testCase, stored, objectType, nowUtc);
            }
        }
    }

    private async Task<SyncRule?> SyncRuleAsync(int id, Dictionary<int, SyncRule?> cache)
    {
        if (!cache.TryGetValue(id, out var rule))
            cache[id] = rule = await Application.Repository.ConnectedSystems.GetSyncRuleAsync(id);
        return rule;
    }

    private async Task<ConnectedSystemObjectType?> ObjectTypeAsync(int? id, Dictionary<int, ConnectedSystemObjectType?> cache)
    {
        if (id is not { } objectTypeId)
            return null;
        if (!cache.TryGetValue(objectTypeId, out var objectType))
            cache[objectTypeId] = objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        return objectType;
    }

    #endregion
}
