// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Tasking;
using Serilog;
using System.Text.Json;

namespace JIM.Application.Servers;

/// <summary>
/// Runs a configuration change preview: everything that is the same whatever surface is being previewed. Adapters
/// answer "what would this do?"; this server decides when to ask, what to record, how to summarise the answer, and
/// what an administrator is told when any of it goes wrong.
///
/// Two rules shape the whole class:
///
/// **A failed preview is failed, not partial.** An evaluation that dies halfway through has seen an arbitrary
/// subset of the population. Nothing computed from that subset is persisted, because a summary drawn from it would
/// under-count without saying so, and an under-count that looks authoritative is worse than no answer: it is the
/// mechanism by which a change gets approved as safe.
///
/// **Progress belongs on the Activity.** The `trg_activities_notify_progress` trigger watches the Activity's
/// Status, Message, ObjectsProcessed and ObjectsToProcess columns and nothing else. Stage transitions recorded only
/// on the preview row would raise no notification and leave the panel silent, so every stage change writes to both.
/// </summary>
public class ConfigurationChangePreviewServer
{
    /// <summary>
    /// How many delta rows are kept per summary group when the result is capped. Per-group rather than global so
    /// every group stays drillable: one enormous group under a global cap would starve every other group of rows,
    /// and the small, surprising group is usually the one worth reading.
    /// </summary>
    public const int MaximumDeltasPerGroup = 1_000;

    /// <summary>
    /// How often, in delta rows, evaluation progress is written back to the Activity. Every row would be a write per
    /// object; the notification listener already coalesces bursts over a 200 ms window, so a finer interval would
    /// buy the panel nothing and cost the database a great deal.
    /// </summary>
    private const int ProgressReportingInterval = 500;

    private readonly JimApplication _application;
    private readonly ConfigurationChangePreviewAdapterRegistry _adapters;

    public ConfigurationChangePreviewServer(JimApplication application, ConfigurationChangePreviewAdapterRegistry adapters)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    /// <summary>
    /// Runs small previews in the host's own process. Set by JIM.Web at startup, following the same pattern as
    /// <see cref="JimApplication.CredentialProtection"/>: the implementation lives in the presentation host and
    /// cannot be constructed from here. Null in JIM.Worker and JIM.Scheduler, and in any host that has not
    /// registered one, in which case every preview goes to the worker; slower for a small preview, never wrong.
    /// </summary>
    public IConfigurationChangePreviewBackgroundRunner? BackgroundRunner { get; set; }

    /// <summary>
    /// Whether <paramref name="surface"/> can be previewed at all. Surfaces with no adapter keep the save-time
    /// acknowledgement and offer no preview; callers ask this rather than discovering it from an exception.
    /// </summary>
    public bool CanPreview(ConfigurationChangePreviewSurface surface) => _adapters.HasAdapterFor(surface);

    /// <summary>
    /// The type a proposal for <paramref name="surface"/> must be, as its adapter declares it. Exposed so
    /// JIM.Worker can reconstruct a queued proposal without the payload having to name its own type.
    /// </summary>
    /// <exception cref="InvalidOperationException">No adapter serves the surface.</exception>
    public Type GetProposalType(ConfigurationChangePreviewSurface surface) => _adapters.Get(surface).ProposalType;

    /// <summary>
    /// Starts a preview and sets its remaining stages running, wherever they belong. The single entry point a
    /// surface calls: where a preview executes is a capacity decision the framework makes from the adapter's cost
    /// estimate, and no caller should have to make it, or be able to get it wrong.
    /// </summary>
    public async Task<ConfigurationChangePreviewStartResult> StartAndDispatchPreviewAsync(ConfigurationChangePreviewRequest request)
    {
        var result = await StartPreviewAsync(request);

        // A proposal that cannot be applied, or a validation stage that failed, has already settled the preview.
        if (result.Failed || result.IsBlocked)
            return result;

        await DispatchAsync(result.ActivityId, request, result.Estimate!);
        return result;
    }

    /// <summary>
    /// Cancels a running preview. Previews running in this process stop directly; the rest are cancelled through
    /// their worker task, which the worker acts on at its next pass.
    /// </summary>
    /// <returns>False when no running preview was found to cancel, in which case it has already finished.</returns>
    public async Task<bool> CancelPreviewAsync(Guid activityId)
    {
        if (BackgroundRunner?.Cancel(activityId) == true)
            return true;

        var workerTask = await _application.Tasking.GetWorkerTaskByActivityIdAsync(activityId);
        if (workerTask is null)
            return false;

        // Request rather than cancel outright: a task the worker is already processing has to be told to stop and
        // given the chance to record that it did, which cancelling the record from underneath it would not allow.
        await _application.Tasking.RequestWorkerTaskCancellationAsync(workerTask.Id);
        return true;
    }

    private async Task DispatchAsync(Guid activityId, ConfigurationChangePreviewRequest request, PreviewCostEstimate estimate)
    {
        var threshold = await _application.ServiceSettings.GetConfigurationChangePreviewWorkerThresholdAsync();
        var runHere = BackgroundRunner is not null && estimate.AffectedObjects <= threshold;

        if (runHere)
        {
            BackgroundRunner!.Enqueue(activityId, request);
            Log.Debug("DispatchAsync: Preview {ActivityId} runs in this process ({Estimate} objects estimated, threshold {Threshold})",
                activityId, estimate.AffectedObjects, threshold);
            return;
        }

        await QueueWorkerTaskAsync(activityId, request, estimate, threshold);
    }

    private async Task QueueWorkerTaskAsync(Guid activityId, ConfigurationChangePreviewRequest request,
        PreviewCostEstimate estimate, int threshold)
    {
        var adapter = _adapters.Get(request.Surface);
        if (!adapter.ProposalType.IsInstanceOfType(request.ProposedConfiguration))
        {
            throw new InvalidOperationException(
                $"The proposed configuration for {request.Surface} is a {request.ProposedConfiguration.GetType().Name}, " +
                $"but its adapter declares {adapter.ProposalType.Name}. A proposal that cannot be serialised as the " +
                "declared type cannot be handed to JIM.Worker.");
        }

        var activity = await _application.Activities.GetActivityAsync(activityId)
                       ?? throw new InvalidOperationException($"Configuration change preview {activityId} has no Activity.");

        var workerTask = new ConfigurationChangePreviewWorkerTask
        {
            Surface = request.Surface,
            TargetId = request.TargetId,
            TargetGuidId = request.TargetGuidId,
            TargetName = request.TargetName,
            ProposedConfigurationPayload = JsonSerializer.Serialize(request.ProposedConfiguration, adapter.ProposalType),
            InitiatedByType = request.InitiatedByType,
            InitiatedById = request.InitiatedById,
            InitiatedByName = request.InitiatedByName,

            // The preview's Activity already exists; this task attaches to it rather than creating another, which
            // is what makes validation and evaluation one Activity rather than two unrelated ones.
            Activity = activity
        };

        var preview = await _application.Repository.ConfigurationChangePreviews.GetPreviewAsync(activityId);
        if (preview is not null)
        {
            preview.DispatchedToWorker = true;
            await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);
        }

        await _application.Tasking.CreateWorkerTaskAsync(workerTask);
        Log.Debug("QueueWorkerTaskAsync: Preview {ActivityId} queued for JIM.Worker ({Estimate} objects estimated, threshold {Threshold})",
            activityId, estimate.AffectedObjects, threshold);
    }

    /// <summary>
    /// Creates the preview's Activity and runs stage 1 (validation) in the caller's thread, so a proposal that
    /// cannot be applied says so immediately instead of after an evaluation that could never have meant anything.
    ///
    /// The remaining stages are not started here. The caller runs them via
    /// <see cref="RunPreviewAsync"/>, in this process or in JIM.Worker; both paths execute the same code and write
    /// the same rows.
    /// </summary>
    /// <exception cref="InvalidOperationException">No adapter serves the requested surface.</exception>
    public async Task<ConfigurationChangePreviewStartResult> StartPreviewAsync(ConfigurationChangePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolved before anything is persisted: a surface with no adapter is a caller error, and failing here
        // leaves no half-born preview Activity behind to explain.
        var adapter = _adapters.Get(request.Surface);

        var activity = BuildActivity(request);
        await _application.Activities.CreateActivityWithTriadAsync(activity, request.InitiatedByType, request.InitiatedById, request.InitiatedByName);

        var preview = new ConfigurationChangePreview
        {
            ActivityId = activity.Id,
            Surface = request.Surface,
            ProposedConfigurationSnapshot = request.ProposedConfigurationSnapshot,
            ValidationStatus = ConfigurationChangePreviewStageStatus.InProgress,
            ValidationStarted = DateTime.UtcNow
        };
        await _application.Repository.ConfigurationChangePreviews.CreatePreviewAsync(preview);

        var context = BuildContext(request, activity.Id);
        List<PreviewValidationFinding> findings;
        PreviewCostEstimate estimate;
        try
        {
            findings = await adapter.ValidateAsync(context) ?? [];
            preview.ValidationFindings = Serialise(findings);
            preview.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            preview.ValidationCompleted = DateTime.UtcNow;

            if (findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking))
            {
                // Nothing downstream is worth computing. Counting the objects a change would affect is a statement
                // about a change that will happen; this one cannot.
                preview.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
                preview.SummaryStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
                preview.DeltasStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
                await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);

                activity.Message = "The proposed configuration cannot be applied";
                await _application.Activities.CompleteActivityWithWarningAsync(activity);
                return new ConfigurationChangePreviewStartResult(activity.Id, findings);
            }

            estimate = await adapter.EstimateCostAsync(context);
            preview.EstimatedAffectedObjects = estimate.AffectedObjects;
            preview.EstimatedDeltaRows = estimate.EstimatedDeltaRows;
            await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);
        }
        catch (Exception ex)
        {
            // Sanctioned broad catch (see src/CLAUDE.md, Activity execution boundaries): an adapter failure that
            // escaped here would leave a preview Activity in progress for ever, with nothing anywhere saying why.
            await FailPreviewAsync(preview, activity, ex, "validating the proposed configuration",
                p =>
                {
                    if (p.ValidationStatus != ConfigurationChangePreviewStageStatus.Complete)
                        p.ValidationStatus = ConfigurationChangePreviewStageStatus.Failed;
                    else
                        // Validation had already finished; what failed was the cost estimate, which is stage 2's
                        // input and has no stage of its own.
                        p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Failed;
                });
            return new ConfigurationChangePreviewStartResult(activity.Id, [], null, Failed: true);
        }

        return new ConfigurationChangePreviewStartResult(activity.Id, findings, estimate);
    }

    /// <summary>
    /// Runs stages 2 to 4: the impact counts, the summary computed from the evaluated delta stream, and the delta
    /// rows kept behind it. Returns when the preview reaches a terminal state; failures are recorded on the preview
    /// and its Activity rather than thrown, because the caller is a background task with nobody to catch them.
    /// </summary>
    /// <param name="activityId">The Activity returned by <see cref="StartPreviewAsync"/>.</param>
    /// <param name="request">
    /// The same request stage 1 ran against. Passed again rather than reconstructed, because the proposed
    /// configuration is an unsaved object that exists only in the caller's hands.
    /// </param>
    /// <param name="cancellationToken">
    /// Honoured between deltas as well as inside the adapter: an administrator who cancels has said they no longer
    /// want the answer, and an adapter that ignores its token should not be able to keep a database connection busy
    /// producing one.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The preview does not exist, or the request describes a different surface from the one the preview was
    /// started for. Evaluating a proposal against the wrong surface's adapter would produce a confident answer to a
    /// question nobody asked.
    /// </exception>
    public async Task RunPreviewAsync(Guid activityId, ConfigurationChangePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var preview = await _application.Repository.ConfigurationChangePreviews.GetPreviewAsync(activityId)
                      ?? throw new InvalidOperationException($"There is no configuration change preview for Activity {activityId}.");

        if (preview.Surface != request.Surface)
            throw new InvalidOperationException(
                $"Preview {activityId} was started for {preview.Surface} but the request describes {request.Surface}.");

        var activity = await _application.Activities.GetActivityAsync(activityId)
                       ?? throw new InvalidOperationException($"Configuration change preview {activityId} has no Activity.");

        if (preview.HasFailed || preview.ImpactCountsStatus == ConfigurationChangePreviewStageStatus.NotApplicable)
        {
            // Stage 1 either failed or blocked the proposal, and settled the remaining stages when it did.
            Log.Debug("RunPreviewAsync: Preview {ActivityId} settled during validation; nothing further to run", activityId);
            return;
        }

        var adapter = _adapters.Get(preview.Surface);
        var context = BuildContext(request, activityId);

        if (!await RunImpactCountsAsync(preview, activity, adapter, context, cancellationToken))
            return;

        if (!await RunEvaluationAsync(preview, activity, adapter, context, cancellationToken))
            return;

        activity.Message = "Preview complete";
        await _application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Stage 2. Returns false when the preview has reached a terminal state and nothing further should run.
    /// </summary>
    private async Task<bool> RunImpactCountsAsync(ConfigurationChangePreview preview, Activity activity,
        IConfigurationChangePreviewAdapter adapter, PreviewContext context, CancellationToken cancellationToken)
    {
        preview.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.InProgress;
        preview.ImpactCountsStarted = DateTime.UtcNow;
        await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);

        activity.Message = "Counting affected objects";
        activity.ObjectsToProcess = ClampToInt(preview.EstimatedDeltaRows);
        await _application.Activities.UpdateActivityAsync(activity);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = await adapter.CountImpactAsync(context) ?? [];
            preview.ImpactCounts = Serialise(counts);
            preview.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete;
            preview.ImpactCountsCompleted = DateTime.UtcNow;
            await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);
            return true;
        }
        catch (OperationCanceledException)
        {
            await CancelPreviewAsync(preview, activity,
                p => p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Cancelled);
            return false;
        }
        catch (Exception ex)
        {
            // Sanctioned broad catch (see src/CLAUDE.md, Activity execution boundaries).
            await FailPreviewAsync(preview, activity, ex, "counting the objects the change would affect",
                p => p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Failed);
            return false;
        }
    }

    /// <summary>
    /// Stages 3 and 4, which are one pass over one stream: the summary is computed from the same deltas that are
    /// persisted beneath it, so the two can never describe different populations. Returns false when the preview
    /// has reached a terminal state.
    /// </summary>
    private async Task<bool> RunEvaluationAsync(ConfigurationChangePreview preview, Activity activity,
        IConfigurationChangePreviewAdapter adapter, PreviewContext context, CancellationToken cancellationToken)
    {
        if (!adapter.ProducesDeltas)
        {
            // A count-only adapter has not looked at individual objects; recording that as "no objects affected"
            // would be an answer it never gave.
            preview.SummaryStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            preview.DeltasStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);
            return true;
        }

        var startedAt = DateTime.UtcNow;
        preview.SummaryStatus = ConfigurationChangePreviewStageStatus.InProgress;
        preview.SummaryStarted = startedAt;
        preview.DeltasStatus = ConfigurationChangePreviewStageStatus.InProgress;
        preview.DeltasStarted = startedAt;
        await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);

        activity.Message = "Evaluating what the change would do";
        await _application.Activities.UpdateActivityAsync(activity);

        var summariser = new PreviewSummariser(MaximumDeltasPerGroup);
        try
        {
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, cancellationToken).WithCancellation(cancellationToken))
            {
                // Checked here as well as inside the adapter: an adapter that ignores its token must not be able to
                // keep an abandoned preview running.
                cancellationToken.ThrowIfCancellationRequested();

                summariser.Add(delta);

                if (summariser.TotalDeltas % ProgressReportingInterval == 0)
                {
                    activity.ObjectsProcessed = ClampToInt(summariser.TotalDeltas);
                    await _application.Activities.UpdateActivityAsync(activity);
                }
            }

            var groups = summariser.BuildGroups(context.ActivityId, await ResolveConnectedSystemNamesAsync(summariser));

            // Persisted only now that the stream has completed. Writing groups as they were discovered would leave
            // a preview that failed at 60% looking like a preview that finished, with counts to match.
            await _application.Repository.ConfigurationChangePreviews.CreatePreviewResultsAsync(groups);

            var completedAt = DateTime.UtcNow;
            preview.SummaryStatus = ConfigurationChangePreviewStageStatus.Complete;
            preview.SummaryCompleted = completedAt;
            preview.DeltasStatus = ConfigurationChangePreviewStageStatus.Complete;
            preview.DeltasCompleted = completedAt;
            preview.DeltaPersistence = summariser.AnyGroupCapped
                ? ConfigurationChangePreviewDeltaPersistence.Capped
                : ConfigurationChangePreviewDeltaPersistence.Full;
            await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);

            activity.ObjectsProcessed = ClampToInt(summariser.TotalDeltas);
            activity.ObjectsToProcess = activity.ObjectsProcessed;
            Log.Debug("RunPreviewAsync: Preview {ActivityId} evaluated {Deltas} deltas into {Groups} groups (capped: {Capped})",
                context.ActivityId, summariser.TotalDeltas, groups.Count, summariser.AnyGroupCapped);
            return true;
        }
        catch (OperationCanceledException)
        {
            await CancelPreviewAsync(preview, activity, p =>
            {
                p.SummaryStatus = ConfigurationChangePreviewStageStatus.Cancelled;
                p.DeltasStatus = ConfigurationChangePreviewStageStatus.Cancelled;
            });
            return false;
        }
        catch (Exception ex)
        {
            // Sanctioned broad catch (see src/CLAUDE.md, Activity execution boundaries).
            await FailPreviewAsync(preview, activity, ex, "evaluating what the change would do", p =>
            {
                p.SummaryStatus = ConfigurationChangePreviewStageStatus.Failed;
                p.DeltasStatus = ConfigurationChangePreviewStageStatus.Failed;
            });
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<int, string>> ResolveConnectedSystemNamesAsync(PreviewSummariser summariser)
    {
        if (summariser.ReferencedConnectedSystemIds.Count == 0)
            return new Dictionary<int, string>();

        var headers = await _application.ConnectedSystems.GetConnectedSystemHeadersAsync();
        return headers.ToDictionary(h => h.Id, h => h.Name);
    }

    private async Task FailPreviewAsync(ConfigurationChangePreview preview, Activity activity, Exception exception,
        string whileDoing, Action<ConfigurationChangePreview> markStages)
    {
        markStages(preview);
        await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);

        // The exception message is about the configuration and the failure, never about the objects evaluated:
        // delta values are personal data and must not reach an Activity's error text any more than a log line.
        Log.Error(exception, "Configuration change preview {ActivityId} failed while {WhileDoing}", activity.Id, whileDoing);
        activity.Message = $"The preview failed while {whileDoing}";
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);
    }

    private async Task CancelPreviewAsync(ConfigurationChangePreview preview, Activity activity,
        Action<ConfigurationChangePreview> markStages)
    {
        markStages(preview);
        await _application.Repository.ConfigurationChangePreviews.UpdatePreviewAsync(preview);

        activity.Message = "The preview was cancelled";
        await _application.Activities.CancelActivityAsync(activity);
    }

    private static Activity BuildActivity(ConfigurationChangePreviewRequest request)
    {
        var activity = new Activity
        {
            TargetType = ConfigurationChangePreviewSurfaces.ToActivityTargetType(request.Surface),
            TargetOperationType = ActivityTargetOperationType.Preview,
            TargetName = request.TargetName,
            Message = "Validating the proposed configuration"
        };

        // The per-target-type id column, so the preview is findable from the object it previewed. Which column that
        // is follows from the surface, exactly as it does for a configuration change Activity.
        switch (request.Surface)
        {
            case ConfigurationChangePreviewSurface.SynchronisationRule:
                activity.SyncRuleId = request.TargetId;
                break;
            case ConfigurationChangePreviewSurface.ConnectedSystem:
                activity.ConnectedSystemId = request.TargetId;
                break;
            case ConfigurationChangePreviewSurface.MetaverseObjectType:
                activity.MetaverseObjectTypeId = request.TargetId;
                break;
            case ConfigurationChangePreviewSurface.MetaverseAttribute:
                activity.MetaverseAttributeId = request.TargetId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Surface,
                    "Preview surface has no Activity target column. Add it here when adding the surface.");
        }

        return activity;
    }

    private static PreviewContext BuildContext(ConfigurationChangePreviewRequest request, Guid activityId) => new()
    {
        Surface = request.Surface,
        ActivityId = activityId,
        TargetId = request.TargetId,
        TargetGuidId = request.TargetGuidId,
        ProposedConfiguration = request.ProposedConfiguration,
        InitiatedByType = request.InitiatedByType,
        InitiatedById = request.InitiatedById,
        InitiatedByName = request.InitiatedByName
    };

    private static string Serialise<T>(List<T> values) => JsonSerializer.Serialize(values);

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;
}
