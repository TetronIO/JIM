// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Log = Serilog.Log;
using ILogger = Serilog.ILogger;

namespace JIM.Worker;

/// <summary>
/// Narrates a Run Profile execution as a sequence of steps (#454): declares the phases the run can
/// perform before it starts, records each transition as it happens, and closes the run out at the
/// end. One reporter lives for the length of one run.
/// </summary>
/// <remarks>
/// <para>
/// The transition rules live in <see cref="ActivityPhaseSet"/>; this type owns the lifetime, the
/// database writes and the Activity message that goes with each step.
/// </para>
/// <para>
/// Narration is cosmetic and must never fail a synchronisation run, so every write here is guarded:
/// a failure is logged and the run continues. Cancellation still propagates.
/// </para>
/// </remarks>
public sealed class ActivityPhaseReporter
{
    private readonly ISyncRepository? _syncRepo;
    private readonly Activity? _activity;
    private readonly ActivityPhaseSet? _phases;
    private readonly ILogger _logger;

    private ActivityPhaseReporter(ISyncRepository? syncRepo, Activity? activity, ActivityPhaseSet? phases, ILogger? logger = null)
    {
        _syncRepo = syncRepo;
        _activity = activity;
        _phases = phases;
        _logger = logger ?? Log.ForContext<ActivityPhaseReporter>();
    }

    /// <summary>
    /// A reporter that records nothing, for callers that do not track phases.
    /// </summary>
    public static ActivityPhaseReporter None { get; } = new(null, null, null);

    /// <summary>
    /// The run's phases as they currently stand. Null when this reporter records nothing.
    /// </summary>
    public IReadOnlyList<ActivityPhase>? Phases => _phases?.Phases;

    /// <summary>
    /// Declares the phases this run can perform (JIM's own, plus any the Connector declares nested
    /// inside the phase that calls it) and records them against the Activity, so an administrator
    /// sees the whole journey from the moment the run starts.
    /// </summary>
    /// <remarks>
    /// A Connector that declares no phases, or throws while declaring them, costs the run nothing:
    /// JIM's own phases are recorded either way.
    /// </remarks>
    public static async Task<ActivityPhaseReporter> StartAsync(
        ISyncRepository syncRepo,
        Activity activity,
        IConnector? connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        ILogger? logger = null)
    {
        var log = logger ?? Log.ForContext<ActivityPhaseReporter>();
        var connectorPhases = GetConnectorPhases(connector, connectedSystem, runProfile, log);
        var phases = ActivityPhaseSet.Declare(
            activity.Id, runProfile.RunType, connectorPhases, GetInapplicablePhaseKeys(connector));
        var reporter = new ActivityPhaseReporter(syncRepo, activity, phases, log);

        if (phases.Phases.Count > 0)
            await reporter.PersistAsync(phases.Phases, "declaring the run's phases");

        return reporter;
    }

    /// <summary>
    /// Moves the run to one of JIM's own phases, and sets the Activity message to go with it.
    /// </summary>
    /// <param name="phaseKey">A <see cref="RunPhaseKeys"/> constant.</param>
    /// <param name="message">
    /// The message to show while the phase runs. Omit it to use the phase's declared name, which is
    /// the right choice whenever the step's name already says everything there is to say.
    /// </param>
    public Task EnterAsync(string phaseKey, string? message = null) => EnterCoreAsync(phaseKey, message);

    /// <summary>
    /// Moves the run to one of the Connector's declared phases, which shows as the step running
    /// inside the JIM phase that called the Connector.
    /// </summary>
    public Task EnterConnectorPhaseAsync(string connectorPhaseKey, string? message = null) =>
        EnterCoreAsync(ActivityPhase.QualifyConnectorKey(connectorPhaseKey), message);

    /// <summary>
    /// Closes the run out: whatever step was running is completed (or recorded as the step the run
    /// failed in), and anything never reached is recorded as skipped.
    /// </summary>
    /// <param name="failed">True when the run failed or was cancelled.</param>
    public async Task FinishAsync(bool failed)
    {
        if (_phases == null)
            return;

        var changed = _phases.Finish(DateTime.UtcNow, failed);
        if (changed.Count > 0)
            await PersistAsync(changed, "closing the run's phases");
    }

    /// <summary>
    /// Builds the progress reporter handed to a Connector on the import side, so the Connector's
    /// phase changes advance the stepper and its narration reaches the Activity message.
    /// </summary>
    /// <param name="reportMessage">
    /// How a narration message reaches the Activity; the import path writes it straight to the
    /// Activity message.
    /// </param>
    /// <param name="reportExpectedObjectCount">
    /// How a Connector's statement of the run's total object count reaches the Activity, or null
    /// where the caller has nowhere to put it.
    /// </param>
    /// <param name="reportObjectsRead">
    /// How a Connector's running count of the objects it has read within the current call
    /// reaches the Activity, or null where the caller has nowhere to put it.
    /// </param>
    public IConnectorProgress CreateConnectorProgress(
        Func<string, Task> reportMessage,
        Func<int, Task>? reportExpectedObjectCount = null,
        Func<int, Task>? reportObjectsRead = null) =>
        new ConnectorProgress(
            report: reportMessage,
            reportExpectedObjectCount: reportExpectedObjectCount,
            reportObjectsRead: reportObjectsRead,
            // A reporter that records nothing hands over no phase delegate, so that a Connector's
            // narration still reaches the Activity through the message path rather than being lost
            // because it arrived attached to a phase change.
            enterPhase: _phases == null
                ? null
                : async (phaseKey, message) => await EnterConnectorPhaseAsync(phaseKey, message),
            logger: _logger);

    private async Task EnterCoreAsync(string phaseKey, string? message)
    {
        if (_phases == null || _syncRepo == null || _activity == null)
            return;

        var changed = _phases.Enter(phaseKey, DateTime.UtcNow, message);
        if (changed.Count > 0)
            await PersistAsync(changed, "recording a phase transition");

        // The message is written even when the transition changed nothing (re-entering the phase
        // already running), because that is how a Connector narrating within a phase reaches the
        // Activity. Fall back to the step's own name so the message and the stepper never disagree.
        var text = string.IsNullOrWhiteSpace(message)
            ? _phases.Phases.SingleOrDefault(p => p.Key == phaseKey)?.Name
            : message;

        if (!string.IsNullOrWhiteSpace(text))
            await GuardAsync(() => _syncRepo.UpdateActivityMessageAsync(_activity, text), "updating the Activity message");
    }

    private async Task PersistAsync(IReadOnlyList<ActivityPhase> phases, string what)
    {
        if (_syncRepo == null)
            return;

        await GuardAsync(() => _syncRepo.SaveActivityPhasesAsync(phases), what);
    }

    private async Task GuardAsync(Func<Task> write, string what)
    {
        try
        {
            await write();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing a step is a cosmetic loss; failing the run over it would not be.
            _logger.Warning(ex, "ActivityPhaseReporter: Failed while {What}. The run continues", LogSanitiser.Sanitise(what));
        }
    }

    /// <summary>
    /// The JIM phases this run cannot perform, given the Connector it runs against, so they are not
    /// shown at all.
    /// </summary>
    /// <remarks>
    /// This is narrower than skipping. A step a run could have taken but did not (deletion detection
    /// on a Delta Import) is worth showing as skipped, because its absence is a fact about the run.
    /// Work the Connector is structurally incapable of is not: a file-based import opens no
    /// connection, so a connection step would appear greyed out on every file-based run, for ever,
    /// saying nothing.
    /// </remarks>
    private static IReadOnlySet<string> GetInapplicablePhaseKeys(IConnector? connector)
    {
        var inapplicable = new HashSet<string>(StringComparer.Ordinal);

        if (connector is not IConnectorImportUsingCalls)
            inapplicable.Add(RunPhaseKeys.ImportConnect);

        return inapplicable;
    }

    private static IReadOnlyList<ConnectorPhase>? GetConnectorPhases(
        IConnector? connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        ILogger logger)
    {
        if (connector is not IConnectorPhases phaseAwareConnector)
            return null;

        try
        {
            return phaseAwareConnector.GetPhases(connectedSystem, runProfile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Connector that cannot describe itself still gets to run; it just narrates less.
            logger.Warning(ex, "ActivityPhaseReporter: Connector {ConnectorName} failed to declare its phases. The run continues with JIM's phases only",
                LogSanitiser.Sanitise(connector.Name));
            return null;
        }
    }
}
