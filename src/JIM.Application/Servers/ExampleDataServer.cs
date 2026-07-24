// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Utilities;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Utility;
// Activity is ambiguous between JIM.Models.Activities.Activity and System.Diagnostics.Activity (used for Stopwatch);
// the configuration-change capture here means the JIM Activity, so alias it explicitly.
using Activity = JIM.Models.Activities.Activity;
using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;
namespace JIM.Application.Servers;

public class ExampleDataServer
{
    #region accessors
    private JimApplication Application { get; }
    #endregion

    #region members
    private readonly object _metaverseObjectLock = new();
    // The expression evaluator used to evaluate attribute-generation expressions. Instantiated directly, as
    // ExportEvaluationServer and the worker's sync processor also do; the underlying compiled-expression cache is static.
    private readonly IExpressionEvaluator _expressionEvaluator = new DynamicExpressoEvaluator();
    #endregion

    internal ExampleDataServer(JimApplication application)
    {
        Application = application;
    }

    #region ExampleDataSets
    public async Task<List<ExampleDataSet>> GetExampleDataSetsAsync()
    {
        return await Application.Repository.ExampleData.GetExampleDataSetsAsync();
    }

    public async Task<List<ExampleDataSetHeader>> GetExampleDataSetHeadersAsync()
    {
        return await Application.Repository.ExampleData.GetExampleDataSetHeadersAsync();
    }

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture, bool withChangeTracking = false)
    {
        return await Application.Repository.ExampleData.GetExampleDataSetAsync(name, culture, withChangeTracking);
    }

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(int id)
    {
        return await Application.Repository.ExampleData.GetExampleDataSetAsync(id);
    }

    public async Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet, MetaverseObject? initiatedBy, string? changeReason = null) =>
        await CreateExampleDataSetCoreAsync(exampleDataSet, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            ds => AuditHelper.SetCreated(ds, initiatedBy));

    public async Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet, ApiKey initiatedByApiKey, string? changeReason = null) =>
        await CreateExampleDataSetCoreAsync(exampleDataSet, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            ds => AuditHelper.SetCreated(ds, initiatedByApiKey));

    private async Task CreateExampleDataSetCoreAsync(ExampleDataSet exampleDataSet, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<ExampleDataSet> setCreated)
    {
        var activity = new Activity
        {
            TargetName = exampleDataSet.Name,
            TargetType = ActivityTargetType.ExampleDataSet,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await createActivityAsync(activity);
        setCreated(exampleDataSet);
        await Application.Repository.ExampleData.CreateExampleDataSetAsync(exampleDataSet);
        await CaptureExampleDataSetConfigurationChangeAsync(activity, exampleDataSet.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet, MetaverseObject? initiatedBy, string? changeReason = null) =>
        await UpdateExampleDataSetCoreAsync(exampleDataSet, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            ds => AuditHelper.SetUpdated(ds, initiatedBy));

    public async Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet, ApiKey initiatedByApiKey, string? changeReason = null) =>
        await UpdateExampleDataSetCoreAsync(exampleDataSet, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            ds => AuditHelper.SetUpdated(ds, initiatedByApiKey));

    private async Task UpdateExampleDataSetCoreAsync(ExampleDataSet exampleDataSet, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<ExampleDataSet> setUpdated)
    {
        var activity = new Activity
        {
            TargetName = exampleDataSet.Name,
            TargetType = ActivityTargetType.ExampleDataSet,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await createActivityAsync(activity);
        setUpdated(exampleDataSet);
        await Application.Repository.ExampleData.UpdateExampleDataSetAsync(exampleDataSet);
        await CaptureExampleDataSetConfigurationChangeAsync(activity, exampleDataSet.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task DeleteExampleDataSetAsync(int exampleDataSetId, MetaverseObject? initiatedBy, string? changeReason = null) =>
        await DeleteExampleDataSetCoreAsync(exampleDataSetId, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));

    public async Task DeleteExampleDataSetAsync(int exampleDataSetId, ApiKey initiatedByApiKey, string? changeReason = null) =>
        await DeleteExampleDataSetCoreAsync(exampleDataSetId, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));

    private async Task DeleteExampleDataSetCoreAsync(int exampleDataSetId, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        // Resolve the entity before removal so the tombstone snapshot and the Activity's target name are complete. A
        // not-found id records no Activity (pass-through to the repository's no-op), so a missing target does not spam
        // the Activity list.
        var exampleDataSet = await Application.Repository.ExampleData.GetExampleDataSetAsync(exampleDataSetId);
        if (exampleDataSet == null)
            return;

        var activity = new Activity
        {
            TargetName = exampleDataSet.Name,
            TargetType = ActivityTargetType.ExampleDataSet,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await createActivityAsync(activity);
        await CaptureExampleDataSetDeletionAsync(activity, exampleDataSet, changeReason);
        await Application.Repository.ExampleData.DeleteExampleDataSetAsync(exampleDataSetId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    // Captures a redacted, versioned snapshot of an Example Data Set onto its audit Activity via the shared capture
    // service; the set is reloaded (with its values) so the value count reflects persisted truth.
    private async Task CaptureExampleDataSetConfigurationChangeAsync(Activity activity, int dataSetId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.ExampleDataSet, dataSetId,
            async hashKey =>
            {
                var dataSet = await Application.Repository.ExampleData.GetExampleDataSetAsync(dataSetId);
                return dataSet == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(dataSet, hashKey);
            },
            $"Example Data Set {dataSetId}");
    }

    // Captures a tombstone snapshot of an Example Data Set onto its delete Activity, before removal.
    private async Task CaptureExampleDataSetDeletionAsync(Activity activity, ExampleDataSet dataSet, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                var persisted = await Application.Repository.ExampleData.GetExampleDataSetAsync(dataSet.Id) ?? dataSet;
                return Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Example Data Set {dataSet.Id}");
    }

    /// <summary>
    /// Records a System-attributed Create Activity and version-1 baseline snapshot for a built-in Example Data Set that
    /// has just been seeded, grouped under the seeding pass's parent Activity, after the seed batch persists (mirroring
    /// <c>SearchServer.RecordSeededPredefinedSearchBaselineAsync</c>). Idempotency is the caller's responsibility:
    /// <see cref="SeedingServer"/> only calls this for sets it created this pass.
    /// </summary>
    internal async Task RecordSeededExampleDataSetBaselineAsync(int dataSetId, string name, Guid parentActivityId)
    {
        var activity = new Activity
        {
            TargetName = name,
            TargetType = ActivityTargetType.ExampleDataSet,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId,
            Message = $"Created built-in Example Data Set '{name}'"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);

        try
        {
            await CaptureExampleDataSetConfigurationChangeAsync(activity, dataSetId, "Built-in Example Data Set created automatically by JIM.");
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }
    #endregion

    #region ExampleDataTemplates
    public async Task<List<ExampleDataTemplate>> GetTemplatesAsync()
    {
        return await Application.Repository.ExampleData.GetTemplatesAsync();
    }

    public async Task<List<ExampleDataTemplateHeader>> GetTemplateHeadersAsync()
    {
        return await Application.Repository.ExampleData.GetTemplateHeadersAsync();
    }

    public async Task<ExampleDataTemplate?> GetTemplateAsync(int id)
    {
        return await Application.Repository.ExampleData.GetTemplateAsync(id);
    }

    public async Task<ExampleDataTemplate?> GetTemplateAsync(string name)
    {
        return await Application.Repository.ExampleData.GetTemplateAsync(name);
    }

    public async Task<ExampleDataTemplateHeader?> GetTemplateHeaderAsync(int id)
    {
        return await Application.Repository.ExampleData.GetTemplateHeaderAsync(id);
    }

    // Example Data Templates have no user-facing CRUD surface yet (the REST controller mutates only Example Data Sets),
    // so these mutators are attributed via the initiator triad and used by seeding today (System). The triad lets a
    // future template-editing UI/API arrive without a signature change; each mutator records a versioned configuration
    // snapshot like every other configuration object.
    public async Task CreateTemplateAsync(ExampleDataTemplate template, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null, Guid? parentActivityId = null)
    {
        var activity = new Activity
        {
            TargetName = template.Name,
            TargetType = ActivityTargetType.ExampleDataTemplate,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        AuditHelper.SetCreated(template, initiatorType, initiatorId, initiatorName);
        await Application.Repository.ExampleData.CreateTemplateAsync(template);
        await CaptureExampleDataTemplateConfigurationChangeAsync(activity, template.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateTemplateAsync(ExampleDataTemplate template, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        var activity = new Activity
        {
            TargetName = template.Name,
            TargetType = ActivityTargetType.ExampleDataTemplate,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        AuditHelper.SetUpdated(template, initiatorType, initiatorId, initiatorName);
        await Application.Repository.ExampleData.UpdateTemplateAsync(template);
        await CaptureExampleDataTemplateConfigurationChangeAsync(activity, template.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task DeleteTemplateAsync(int templateId, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        var template = await Application.Repository.ExampleData.GetTemplateAsync(templateId);
        if (template == null)
            return;

        var activity = new Activity
        {
            TargetName = template.Name,
            TargetType = ActivityTargetType.ExampleDataTemplate,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await CaptureExampleDataTemplateDeletionAsync(activity, template, changeReason);
        await Application.Repository.ExampleData.DeleteTemplateAsync(templateId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    private async Task CaptureExampleDataTemplateConfigurationChangeAsync(Activity activity, int templateId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.ExampleDataTemplate, templateId,
            async hashKey =>
            {
                var template = await Application.Repository.ExampleData.GetTemplateAsync(templateId);
                return template == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(template, hashKey);
            },
            $"Example Data Template {templateId}");
    }

    private async Task CaptureExampleDataTemplateDeletionAsync(Activity activity, ExampleDataTemplate template, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                var persisted = await Application.Repository.ExampleData.GetTemplateAsync(template.Id) ?? template;
                return Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Example Data Template {template.Id}");
    }

    /// <summary>
    /// Records a System-attributed Create Activity and version-1 baseline snapshot for a built-in Example Data Template
    /// that has just been seeded, grouped under the seeding pass's parent Activity, after the seed batch persists
    /// (mirroring <see cref="RecordSeededExampleDataSetBaselineAsync"/>).
    /// </summary>
    internal async Task RecordSeededExampleDataTemplateBaselineAsync(int templateId, string name, Guid parentActivityId)
    {
        var activity = new Activity
        {
            TargetName = name,
            TargetType = ActivityTargetType.ExampleDataTemplate,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId,
            Message = $"Created built-in Example Data Template '{name}'"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);

        try
        {
            await CaptureExampleDataTemplateConfigurationChangeAsync(activity, templateId, "Built-in Example Data Template created automatically by JIM.");
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Executes a data generation template to create Metaverse Objects.
    /// </summary>
    /// <param name="templateId">The ID of the template to execute.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <param name="progressCallback">Optional callback for reporting progress. Parameters are (totalObjects, objectsProcessed, message).</param>
    /// <param name="progressUpdateInterval">How often to report progress. If null, progress is only reported after each object type completes.</param>
    /// <param name="batchSize">Batch size for database persistence. Smaller batches reduce memory pressure.</param>
    /// <returns>The number of objects created.</returns>
    public async Task<int> ExecuteTemplateAsync(
        int templateId,
        CancellationToken cancellationToken,
        Func<int, int, string?, Task>? progressCallback = null,
        TimeSpan? progressUpdateInterval = null,
        int batchSize = 500,
        ActivityInitiatorType initiatedByType = ActivityInitiatorType.NotSet,
        Guid? initiatedById = null,
        string? initiatedByName = null)
    {
        // get the entire template
        // enumerate the object types
        // build the objects << probably fine up to a point, then it might consume too much ram
        // submit in bulk to data layer << probably fine up to a point, then EF might blow a gasket

        Log.Information($"ExecuteTemplateAsync: Generating data...");
        var totalTimeStopwatch = new Stopwatch();
        var objectPreparationStopwatch = new Stopwatch();
        totalTimeStopwatch.Start();
        objectPreparationStopwatch.Start();
        var totalObjectsCreated = 0;
        var getTemplateStopwatch = Stopwatch.StartNew();
        var template = await GetTemplateAsync(templateId);
        getTemplateStopwatch.Stop();
        Log.Verbose($"ExecuteTemplateAsync: get template took: {getTemplateStopwatch.Elapsed}");

        if (template == null)
            throw new ArgumentException("No template found with that id");

        template.Validate();

        // Calculate total objects to create for progress tracking
        var totalObjectsToCreate = template.ObjectTypes.Sum(ot => ot.ObjectsToCreate);
        // Use an array to hold the counter - arrays are reference types so they work correctly
        // with closures and allow thread-safe access via Interlocked/Volatile
        var objectsGeneratedHolder = new int[1];

        // The job runs in equally-weighted phases, each processing every object once: generation, an optional
        // change-history build (only when MVO change tracking is on), then persistence. Reporting each phase's own
        // 0->total count would sweep the progress bar to 100% two or three times; instead every phase's local count
        // is mapped onto a single continuous 0->total overall count, so the Activity progress bar advances once from
        // 0% to 100% across the whole job rather than racing to 100% during generation and then freezing there while
        // persistence (frequently the longest phase) runs. Fetched once here so the phase weighting is known up front.
        var changeTrackingEnabled = await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();
        const int generationPhase = 0;
        var changeHistoryPhase = changeTrackingEnabled ? 1 : -1;
        var persistencePhase = changeTrackingEnabled ? 2 : 1;
        var phaseCount = changeTrackingEnabled ? 3 : 2;
        var reportInterval = progressUpdateInterval ?? TimeSpan.FromSeconds(2);

        // Maps a phase-local processed count onto the overall 0->total progress value (see CalculateOverallProgress)
        // and reports it. Guards the no-callback case; the empty-template divide-by-zero is handled in the calculation.
        async Task ReportOverallProgressAsync(int phase, int phaseProcessed, string? message)
        {
            if (progressCallback == null)
                return;

            var overallProcessed = CalculateOverallProgress(phase, phaseProcessed, totalObjectsToCreate, phaseCount);
            await progressCallback(totalObjectsToCreate, overallProcessed, message);
        }

        // Report initial progress (nothing processed yet)
        await ReportOverallProgressAsync(generationPhase, 0, "Generating objects...");

        // Generation progress is reported from a dedicated background task, NOT from inside the parallel generation
        // loop below. Reporting inline meant a generation thread ran the asynchronous Activity database write
        // synchronously (via GetAwaiter().GetResult()) while holding _metaverseObjectLock, the same lock every other
        // generation thread needs to add its object. That serialised the whole loop behind a blocking I/O call and
        // starved the thread pool, so generating the built-in 10,000-object template crawled at roughly one object per
        // second at ~0% CPU. The loop now only increments the atomic counter; this task samples it on an interval and
        // reports asynchronously, holding no lock. It is cancelled once generation completes (before any further
        // database work).
        using var progressReporterCts = new CancellationTokenSource();
        var progressReporterTask = progressCallback == null
            ? Task.CompletedTask
            : ReportGenerationProgressAsync(
                generated => ReportOverallProgressAsync(generationPhase, generated, "Generating objects..."),
                objectsGeneratedHolder,
                reportInterval,
                progressReporterCts.Token);

        // object type dependency graph needs considering
        // for now we should probably just advise people to add template object types in reverse order to how they're referenced.
        // note: entity framework might handle dependency sequencing for us at time of persistence

        var random = new Random();
        var metaverseObjectsToCreate = new List<MetaverseObject>();
        var trackerStore = new ExampleDataValueTrackerStore();
            
        // we've had issues with EF not returning values for example datasets when retrieving the template
        // so we're going to get all the example datasets referenced in a template separately and passing them in as needed.
        var exampleDataSets = new List<ExampleDataSet>();
        foreach (var datasetInstance in from objectType in template.ObjectTypes from templateAttribute in objectType.TemplateAttributes from datasetInstance in templateAttribute.ExampleDataSetInstances select datasetInstance)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("ExecuteTemplateAsync: Cancellation requested. Returning from data set processing prematurely.");
                return 0;
            }

            if (datasetInstance?.ExampleDataSet == null || exampleDataSets.Any(q => q.Id == datasetInstance.ExampleDataSet.Id)) 
                continue;
                
            var exampleDataSet = await GetExampleDataSetAsync(datasetInstance.ExampleDataSet.Id);
            if (exampleDataSet != null)
                exampleDataSets.Add(exampleDataSet);
        }

        foreach (var objectType in template.ObjectTypes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("ExecuteTemplateAsync: Cancellation requested. Returning from object type processing prematurely.");
                return 0;
            }

            var objectTypeStopWatch = Stopwatch.StartNew();
            Log.Verbose($"ExecuteTemplateAsync: Processing Metaverse Object Type: {objectType.MetaverseObjectType.Name}");
            var trackers = trackerStore;
            var create = metaverseObjectsToCreate;
            // Order attributes so that any attribute an expression (or conditional dependency) references is generated
            // first. Computed once per object type (the dependency graph is static across generated objects), and any
            // circular dependency throws here, before generation begins. See ExampleDataObjectType.
            var orderedTemplateAttributes = objectType.GetTemplateAttributesInDependencyOrder();
            Parallel.For(0, objectType.ObjectsToCreate,
                index =>
                {
                    var metaverseObject = new MetaverseObject
                    {
                        Type = objectType.MetaverseObjectType,
                        Origin = MetaverseObjectOrigin.Internal
                    };
                    foreach (var templateAttribute in orderedTemplateAttributes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Log.Verbose("ExecuteTemplateAsync: Cancellation requested. Returning from attribute processing prematurely.");
                            return;
                        }

                        // only supporting Metaverse attributes for now.
                        // generating values for Connector Space values will have to come later, subject to demand
                        if (templateAttribute.MetaverseAttribute != null)
                        {
                            // is this attribute dependent upon another?
                            if (templateAttribute.AttributeDependency != null)
                            {
                                // get the dependent attribute value
                                var dependentAttributeValue = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Id == templateAttribute.AttributeDependency.MetaverseAttribute.Id);
                                if (dependentAttributeValue == null)
                                {
                                    // there's no dependent attribute, so nothing to compare. do not generate a value
                                    continue;
                                }

                                if (templateAttribute.AttributeDependency.ComparisonType == ComparisonType.Equals)
                                {
                                    if (dependentAttributeValue.StringValue != templateAttribute.AttributeDependency.StringValue)
                                    {
                                        Log.Debug($"ExecuteTemplateAsync: Not generating {templateAttribute.MetaverseAttribute.Name} attribute value, as dependent attribute value '{dependentAttributeValue.StringValue}' does not equal '{templateAttribute.AttributeDependency.StringValue}'");
                                        continue;
                                    }
                                }
                                else
                                {
                                    throw new NotSupportedException("Not currently supporting ComparisonTypes other than Equals");
                                }
                            }

                            // handle each attribute type in dedicated functions
                            switch (templateAttribute.MetaverseAttribute.Type)
                            {
                                case AttributeDataType.Text:
                                    GenerateMetaverseStringValue(metaverseObject, templateAttribute, exampleDataSets, random, trackers);
                                    break;
                                case AttributeDataType.Guid:
                                    GenerateMetaverseGuidValue(metaverseObject, templateAttribute);
                                    break;
                                case AttributeDataType.Number:
                                    GenerateMetaverseNumberValue(metaverseObject, templateAttribute, random, trackers);
                                    break;
                                case AttributeDataType.LongNumber:
                                    GenerateMetaverseLongNumberValue(metaverseObject, templateAttribute, random, trackers);
                                    break;
                                case AttributeDataType.Decimal:
                                    GenerateMetaverseDecimalValue(metaverseObject, templateAttribute, random, trackers);
                                    break;
                                case AttributeDataType.DateTime:
                                    GenerateMetaverseDateTimeValue(metaverseObject, templateAttribute, random);
                                    break;
                                case AttributeDataType.Boolean:
                                    GenerateMetaverseBooleanValue(metaverseObject, templateAttribute, random);
                                    break;
                                case AttributeDataType.Reference:
                                    GenerateMetaverseReferenceValue(metaverseObject, templateAttribute, random, create);
                                    break;
                                case AttributeDataType.NotSet:
                                    break;
                                case AttributeDataType.Binary:
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                    }

                    lock (_metaverseObjectLock)
                        create.Add(metaverseObject);

                    // Atomic counters only; the background reporter (see above) reads objectsGeneratedHolder and does
                    // the asynchronous progress write off this thread, so the loop never blocks on I/O.
                    Interlocked.Increment(ref totalObjectsCreated);
                    Interlocked.Increment(ref objectsGeneratedHolder[0]);
                });

            // user manager attributes need assigning after all users have been prepared
            GenerateManagerAssignments(metaverseObjectsToCreate, objectType, random);

            objectTypeStopWatch.Stop();
            Log.Information($"ExecuteTemplateAsync: It took {objectTypeStopWatch.Elapsed} to process the {objectType.MetaverseObjectType.Name} Metaverse Object Type");
        }

        // generation is complete: stop the background progress reporter before the change-history and persistence work
        // below touches this database context, and surface any failure the reporter captured.
        progressReporterCts.Cancel();
        await progressReporterTask;

        // Snap the bar to the end of the generation segment before the next phase begins, in case the last interval
        // sample landed below the final count.
        await ReportOverallProgressAsync(generationPhase, totalObjectsToCreate, "Generating objects...");

        // ensure that attribute population percentage values are respected
        // do this by assigning all attributes with values (done), then go and randomly delete the required amount
        RemoveUnnecessaryAttributeValues(template, metaverseObjectsToCreate, random);

        // Populate CachedDisplayName from the generated attribute values
        foreach (var mvo in metaverseObjectsToCreate)
        {
            var displayNameAv = mvo.AttributeValues
                .SingleOrDefault(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName);
            mvo.CachedDisplayName = displayNameAv?.StringValue;
        }

        Log.Information($"ExecuteTemplateAsync: Generated {metaverseObjectsToCreate.Count:N0} objects");
        objectPreparationStopwatch.Stop();

        if (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("ExecuteTemplateAsync: Cancellation requested. Returning after removing unecessary attributes prematurely.");
            return 0;
        }

        // Create change history records for each generated object (if change tracking is enabled). This is the second
        // progress phase: in-memory work, but O(objects), so it reports its own progress (throttled by the report
        // interval) rather than leaving the bar frozen between the generation and persistence phases. The loop is
        // sequential and holds no lock, so an awaited progress write here is safe (unlike the parallel generation loop).
        if (changeTrackingEnabled)
        {
            var changeTrackingStopwatch = Stopwatch.StartNew();
            Log.Information("ExecuteTemplateAsync: Creating change history records for {Count:N0} objects...", metaverseObjectsToCreate.Count);
            var emptyRemovals = new List<MetaverseObjectAttributeValue>();
            var changeHistoryReportStopwatch = Stopwatch.StartNew();
            var changeHistoryIndex = 0;
            foreach (var mvo in metaverseObjectsToCreate)
            {
                await Application.Metaverse.CreateMetaverseObjectChangeAsync(
                    mvo,
                    mvo.AttributeValues,
                    emptyRemovals,
                    initiatedByType,
                    initiatedById,
                    initiatedByName,
                    ObjectChangeType.Created,
                    MetaverseObjectChangeInitiatorType.ExampleData);

                changeHistoryIndex++;
                if (changeHistoryReportStopwatch.Elapsed >= reportInterval)
                {
                    changeHistoryReportStopwatch.Restart();
                    await ReportOverallProgressAsync(changeHistoryPhase, changeHistoryIndex, "Recording change history...");
                }
            }
            changeTrackingStopwatch.Stop();
            Log.Information("ExecuteTemplateAsync: Change history records created in {Elapsed}", changeTrackingStopwatch.Elapsed);
        }

        // Entering the persistence phase (the final progress phase), nothing persisted yet.
        await ReportOverallProgressAsync(persistencePhase, 0, "Persisting to database...");

        // FK scalar fixup before persistence.
        //
        // The generator builds each MetaverseObjectAttributeValue with the Attribute navigation set
        // (av.Attribute = templateAttribute.MetaverseAttribute) but leaves the AttributeId scalar at
        // its default of 0. EF's previous AddRange path silently filled the FK from the navigation;
        // the raw-SQL/COPY persistence path on the worker hot path writes av.AttributeId directly
        // and would fail with a foreign key violation against MetaverseAttributes.
        //
        // We populate the FK scalar here, at the boundary between generation and persistence,
        // because (a) it's the right architectural seam (the generator's contract is "produce the
        // graph"; the persistence layer's contract is "write what you're given"), and (b) doing it
        // once per template execution is cheaper than threading the FK through every Generate*
        // helper. ReferenceValueId is fixed up later by CreateMetaverseObjectsBulkAsync itself.
        foreach (var av in metaverseObjectsToCreate.SelectMany(mvo => mvo.AttributeValues).Where(av => av.AttributeId == 0 && av.Attribute is not null))
            av.AttributeId = av.Attribute.Id;

        // submit Metaverse Objects to data layer for creation (batched for memory efficiency and progress)
        var persistenceStopwatch = new Stopwatch();
        persistenceStopwatch.Start();

        // Persistence is the final progress phase; map each batch's real persisted count onto the overall progress
        // value so the bar advances through this phase (previously it was pinned at 100% here and only the message
        // moved). A rolling ETA is surfaced in the message.
        Func<PersistenceProgress, Task>? persistenceProgressCallback = null;
        if (progressCallback != null)
        {
            persistenceProgressCallback = async progress =>
            {
                var message = FormatPersistenceProgressMessage(progress);
                await ReportOverallProgressAsync(persistencePhase, progress.ObjectsPersisted, message);
            };
        }

        try
        {
            await Application.Repository.ExampleData.CreateMetaverseObjectsAsync(
                metaverseObjectsToCreate,
                batchSize,
                cancellationToken,
                persistenceProgressCallback);
        }
        catch (OperationCanceledException ex)
        {
            Log.Information(ex, "ExecuteTemplateAsync: Template '{template.Name}' object persistence did not complete as cancellation was requested.");
        }

        persistenceStopwatch.Stop();
        totalTimeStopwatch.Stop();
        Log.Information($"ExecuteTemplateAsync: Template '{template.Name}' complete. {totalObjectsCreated:N0} objects prepared in {objectPreparationStopwatch.Elapsed}. Persisted in {persistenceStopwatch.Elapsed}. Total time: {totalTimeStopwatch.Elapsed}");

        // trying to help garbage collection along. data generation results in a lot of ram usage.
        metaverseObjectsToCreate.Clear();

        return totalObjectsCreated;
    }

    /// <summary>
    /// Periodically reports generation progress (the current value of the shared atomic object counter) to the supplied
    /// callback from a dedicated task, so the parallel generation loop never blocks on the asynchronous progress write.
    /// Runs until cancelled, which the caller does once generation has finished. Any failure other than cancellation
    /// faults the returned task and is surfaced when the caller awaits it.
    /// </summary>
    private static async Task ReportGenerationProgressAsync(
        Func<int, Task> reportGenerated,
        int[] objectsGeneratedHolder,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                var generated = Volatile.Read(ref objectsGeneratedHolder[0]);
                await reportGenerated(generated);
            }
        }
        catch (OperationCanceledException)
        {
            // expected when generation completes and the reporter is cancelled
        }
    }

    /// <summary>
    /// Maps a phase-local processed count onto the single continuous overall progress count that spans all
    /// equally-weighted phases of a template execution (generation, an optional change-history build, and
    /// persistence). Each phase processes every object once, so the overall count is
    /// (phase * total + phaseProcessed) / phaseCount, with phaseProcessed clamped to [0, total] and the result
    /// rounded to the nearest object. This is what lets the Activity progress bar advance once from 0% to 100%
    /// across the whole job instead of sweeping to 100% per phase. Returns 0 for an empty template (or a
    /// non-positive phase count), avoiding a divide-by-zero.
    /// </summary>
    /// <param name="phase">Zero-based index of the current phase.</param>
    /// <param name="phaseProcessed">Objects processed so far within the current phase.</param>
    /// <param name="totalObjects">Total objects the template creates (the per-phase object count).</param>
    /// <param name="phaseCount">Number of active phases (2 without change tracking, 3 with).</param>
    internal static int CalculateOverallProgress(int phase, int phaseProcessed, int totalObjects, int phaseCount)
    {
        if (totalObjects <= 0 || phaseCount <= 0)
            return 0;

        var clamped = Math.Clamp(phaseProcessed, 0, totalObjects);
        return (int)Math.Round(((double)phase * totalObjects + clamped) / phaseCount);
    }

    /// <summary>
    /// Formats the per-batch persistence-progress payload into a single human-readable message
    /// suitable for the Activity UI. Includes the batch counter and a rolling ETA derived from the
    /// elapsed persistence time, omitting the ETA on the first batch (when we have no useful rate
    /// measurement) and on the final batch (when there is nothing left to do). The object counter is
    /// deliberately not repeated here: the progress bar's overall count already conveys quantity.
    /// </summary>
    internal static string FormatPersistenceProgressMessage(PersistenceProgress progress)
    {
        var baseMessage = $"Persisting to database... batch {progress.BatchIndex:N0}/{progress.BatchCount:N0}";

        // No ETA on batch 1 (no rate yet) or once we're done
        if (progress.BatchIndex <= 1 || progress.ObjectsPersisted >= progress.TotalObjects || progress.Elapsed <= TimeSpan.Zero)
            return baseMessage;

        var remainingObjects = progress.TotalObjects - progress.ObjectsPersisted;
        var msPerObject = progress.Elapsed.TotalMilliseconds / progress.ObjectsPersisted;
        var etaMs = msPerObject * remainingObjects;
        if (double.IsNaN(etaMs) || double.IsInfinity(etaMs) || etaMs <= 0)
            return baseMessage;

        var eta = TimeSpan.FromMilliseconds(etaMs);
        return $"{baseMessage}, ETA {FormatEta(eta)}";
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
            return $"{(int)eta.TotalHours:N0}h {eta.Minutes:D2}m";
        if (eta.TotalMinutes >= 1)
            return $"{eta.Minutes:D2}m {eta.Seconds:D2}s";
        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds)):N0}s";
    }
    #endregion

    #region Attribute Generation
    private void GenerateMetaverseStringValue(
        MetaverseObject metaverseObject,
        ExampleDataTemplateAttribute dataGenerationTemplateAttribute,
        IEnumerable<ExampleDataSet> exampleDataSets,
        Random random,
        ExampleDataValueTrackerStore trackerStore)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        // expression-based generation: evaluate the expression against the object's already-generated attributes.
        // handled before the pattern/data-set paths because an expression fully determines the value.
        if (dataGenerationTemplateAttribute.IsUsingExpression())
        {
            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                StringValue = EvaluateAttributeExpression(metaverseObject, dataGenerationTemplateAttribute)
            });
            return;
        }

        // a string attribute can have a string type or number type value assigned
        if (dataGenerationTemplateAttribute.IsUsingStrings())
        {
            // logic:
            // - if no pattern: handle one or more data set value assignments
            // - if pattern: replace attribute vars, replace system vars and replace example data set vars

            string output;
            if (string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern) && dataGenerationTemplateAttribute.ExampleDataSetInstances.Count == 1)
            {
                // for some reason, this sometimes loads with zero values and an exception is thrown
                // no idea why. need to spend time trying to diagnose this. For now, skip the scenario.
                if (dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values.Count == 0)
                {
                    //Log.Error("GenerateMetaverseStringValue: dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values.Count was zero!");
                    //return;

                    dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet = exampleDataSets.Single(q => q.Id == dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Id);
                }

                // single example-data set based
                var valueIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values.Count);
                output = dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values[valueIndex].StringValue;
            }
            else if (string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern) && dataGenerationTemplateAttribute.ExampleDataSetInstances.Count > 1)
            {
                // multiple example-data set based:
                // just choose randomly a value from across the datasets. simplest for now
                // would prefer to end up with an even distribution of values from across the datasets, but I ran out of time.                 
                var dataSetIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSetInstances.Count);

                // for some reason, Firstnames Female sometimes loads with zero values and an exception is thrown
                // no idea why. need to spend time trying to diagnose this. For now, skip the scenario.
                if (dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values.Count == 0)
                {
                    //Log.Error("GenerateMetaverseStringValue: dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values.Count was zero!");
                    //return;

                    dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet = exampleDataSets.Single(q => q.Id == dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Id);
                }

                var valueIndexMaxValue = dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values.Count;
                var valueIndex = random.Next(0, valueIndexMaxValue);

                output = dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values[valueIndex].StringValue;
            }
            else if (!string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern))
            {
                // pattern generation:
                // parse out the attribute variables {var} and system variables [var]
                // use regex to do this. keep it simple for now, just replace what you find
                // later on we can look at encapsulation, i.e. functions around vars, and functions around functions.
                // replace attribute vars first, then check system vars, i.e. uniqueness ids against complete generated string.
                output = ReplaceAttributeVariables(metaverseObject, dataGenerationTemplateAttribute.Pattern);
                output = ReplaceSystemVariables(metaverseObject, dataGenerationTemplateAttribute.MetaverseAttribute, trackerStore, output);
                output = ReplaceExampleDataSetVariables(metaverseObject, dataGenerationTemplateAttribute.MetaverseAttribute, dataGenerationTemplateAttribute.ExampleDataSetInstances, trackerStore, random, output);
            }
            else if (dataGenerationTemplateAttribute.WeightedStringValues is { Count: > 0 })
            {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                output = dataGenerationTemplateAttribute.WeightedStringValues.RandomElementByWeight(x => x.Weight).Value;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            }
            else
            {
                throw new InvalidDataException("ExampleDataTemplateAttribute string attribute configuration not as expected");
            }

            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                StringValue = output
            });
        }
        else if (dataGenerationTemplateAttribute.IsUsingNumbers())
        {
            var numberValue = GenerateNumberValue(metaverseObject.Type, dataGenerationTemplateAttribute, random, trackerStore);
            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                StringValue = numberValue.ToString()
            });
        }
        else
        {
            throw new ArgumentException("dataGenerationTemplateAttribute isn't using strings or numbers on a string attribute type");
        }
    }

    /// <summary>
    /// Evaluates an attribute's generation expression against the Metaverse Object's already-generated attribute values
    /// (exposed to the expression via the mv["Attribute Name"] accessor) and returns the resulting string.
    /// Reuses the shared DynamicExpresso evaluator. A failed evaluation throws, failing the template execution with an
    /// attributed error rather than silently producing a blank value.
    /// </summary>
    private string EvaluateAttributeExpression(MetaverseObject metaverseObject, ExampleDataTemplateAttribute templateAttribute)
    {
        var attributeName = templateAttribute.MetaverseAttribute!.Name;
        var context = BuildExpressionContext(metaverseObject);

        // Test() evaluates and captures any failure as a result rather than throwing, so we attribute the error here.
        var result = _expressionEvaluator.Test(templateAttribute.Expression!, context);
        if (!result.IsValid)
            throw new InvalidDataException($"ExampleDataServer: failed to evaluate generation expression for attribute '{attributeName}': {result.ErrorMessage}");

        return result.Result?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Builds an <see cref="ExpressionContext"/> exposing the Metaverse Object's already-generated attribute values via
    /// the mv accessor. There is no Connected System Object during generation, so the cs accessor is empty.
    /// </summary>
    private static ExpressionContext BuildExpressionContext(MetaverseObject metaverseObject)
    {
        var mv = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attributeValues in metaverseObject.AttributeValues.Where(av => av.Attribute != null).GroupBy(av => av.Attribute.Name))
        {
            var values = attributeValues.ToList();
            mv[attributeValues.Key] = values.Count == 1
                ? GetExpressionScalarValue(values[0])
                : values.Select(GetExpressionScalarValue).Where(v => v != null).Select(v => v!.ToString()).ToArray();
        }

        return new ExpressionContext(mv);
    }

    /// <summary>
    /// Returns the most appropriate scalar value of a Metaverse Object attribute value for use in an expression.
    /// </summary>
    private static object? GetExpressionScalarValue(MetaverseObjectAttributeValue attributeValue)
    {
        if (attributeValue.StringValue != null)
            return attributeValue.StringValue;
        if (attributeValue.IntValue.HasValue)
            return attributeValue.IntValue.Value;
        if (attributeValue.LongValue.HasValue)
            return attributeValue.LongValue.Value;
        if (attributeValue.DecimalValue.HasValue)
            return attributeValue.DecimalValue.Value;
        if (attributeValue.BoolValue.HasValue)
            return attributeValue.BoolValue.Value;
        if (attributeValue.DateTimeValue.HasValue)
            return attributeValue.DateTimeValue.Value;
        if (attributeValue.GuidValue.HasValue)
            return attributeValue.GuidValue.Value;

        return null;
    }

    private static void GenerateMetaverseGuidValue(MetaverseObject metaverseObject, ExampleDataTemplateAttribute dataGenerationTemplateAttribute)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            GuidValue = Guid.NewGuid()
        });
    }

    private void GenerateMetaverseNumberValue(
        MetaverseObject metaverseObject,
        ExampleDataTemplateAttribute dataGenerationTemplateAttribute,
        Random random,
        ExampleDataValueTrackerStore trackerStore)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        var value = GenerateNumberValue(metaverseObject.Type, dataGenerationTemplateAttribute, random, trackerStore);
        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            IntValue = value
        });
    }

    private void GenerateMetaverseLongNumberValue(
        MetaverseObject metaverseObject,
        ExampleDataTemplateAttribute dataGenerationTemplateAttribute,
        Random random,
        ExampleDataValueTrackerStore trackerStore)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        // Generate a long value - for now, use int generator and cast to long
        var value = GenerateNumberValue(metaverseObject.Type, dataGenerationTemplateAttribute, random, trackerStore);
        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            LongValue = value
        });
    }

    private void GenerateMetaverseDecimalValue(
        MetaverseObject metaverseObject,
        ExampleDataTemplateAttribute dataGenerationTemplateAttribute,
        Random random,
        ExampleDataValueTrackerStore trackerStore)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        // Generate a decimal value - for now, use the int generator (an int widens to decimal exactly),
        // mirroring the LongNumber approach above.
        var value = GenerateNumberValue(metaverseObject.Type, dataGenerationTemplateAttribute, random, trackerStore);
        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            DecimalValue = value
        });
    }

    private static void GenerateMetaverseDateTimeValue(MetaverseObject metaverseObject, ExampleDataTemplateAttribute dataGenerationTemplateAttribute, Random random)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        var startDate = DateTime.MinValue;
        var endDate = DateTime.MaxValue;
        if (dataGenerationTemplateAttribute is { MinDate: not null, MaxDate: not null })
        {
            // between two dates
            startDate = dataGenerationTemplateAttribute.MinDate.Value;
            endDate = dataGenerationTemplateAttribute.MaxDate.Value;
        }
        else if (dataGenerationTemplateAttribute is { MinDate: not null, MaxDate: null })
        {
            // just a min date
            startDate = dataGenerationTemplateAttribute.MinDate.Value;
        }
        else if (!dataGenerationTemplateAttribute.MinDate.HasValue && dataGenerationTemplateAttribute.MaxDate.HasValue)
        {
            // just a max date
            endDate = dataGenerationTemplateAttribute.MaxDate.Value;
        }

        var timeSpan = endDate - startDate;
        var newSpan = new TimeSpan(0, random.Next(0, (int)timeSpan.TotalMinutes), 0);
        var date = startDate + newSpan;

        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            DateTimeValue = date
        });
    }

    private static void GenerateMetaverseBooleanValue(MetaverseObject metaverseObject, ExampleDataTemplateAttribute dataGenerationTemplateAttribute, Random random)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        bool value;
        //if (dataGenerationTemplateAttribute.BoolTrueDistribution.HasValue)
        //{
        // a certain number of true values are required over the total number of objects created
        // TODO (#877): implement true value distribution logic
        //}
        //else
        //{
        // bool should be random
        value = Convert.ToBoolean(random.Next(0, 2));
        //}

        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            BoolValue = value
        });
    }

    private static void GenerateMetaverseReferenceValue(MetaverseObject metaverseObject, ExampleDataTemplateAttribute templateAttribute, Random random, List<MetaverseObject> metaverseObjects)
    {
        if (templateAttribute.MetaverseAttribute == null)
            return;

        // skip if this is for a user manager attribute, that's specially handled elsewhere
        if (metaverseObject.Type.Name == Constants.BuiltInObjectTypes.User && templateAttribute.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager)
            return;


        // debug point. q was null in the query below for some reason. haven't been able to catch it yet
        if (metaverseObjects == null)
        {
            return;
        }

        // is this going to be slow?
        var metaverseObjectsOfTypes = metaverseObjects.Where(q => q != null &&
                                                                  templateAttribute.ReferenceMetaverseObjectTypes != null &&
                                                                  templateAttribute.ReferenceMetaverseObjectTypes.Contains(q.Type)).ToList();

        if (metaverseObjectsOfTypes.Count == 0)
            return;

        if (templateAttribute.MetaverseAttribute.AttributePlurality == AttributePlurality.SingleValued)
        {
            // pick a random Metaverse Object and assign
            var referencedMetaverseObjectIndex = random.Next(0, metaverseObjectsOfTypes.Count);
            var referencedMetaverseObject = metaverseObjectsOfTypes[referencedMetaverseObjectIndex];
            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = templateAttribute.MetaverseAttribute,
                ReferenceValue = referencedMetaverseObject
            });
        }
        else
        {
            // multi-valued attribute
            // determine how many values to pick
            var min = templateAttribute.MvaRefMinAssignments ?? 0;
            var max = templateAttribute.MvaRefMaxAssignments ?? metaverseObjectsOfTypes.Count;
            var attributeValuesToCreate = random.Next(min, max);

            for (var i = 0; i < attributeValuesToCreate; i++)
            {
                var referencedObject = metaverseObjectsOfTypes[random.Next(0, metaverseObjectsOfTypes.Count)];
                metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = templateAttribute.MetaverseAttribute,
                    ReferenceValue = referencedObject
                });
            }
        }
    }

    private void GenerateManagerAssignments(List<MetaverseObject> metaverseObjectsToCreate, ExampleDataObjectType objectType, Random random)
    {
        Log.Verbose("GenerateManagerAssignments: Started...");
        var templateManagerAttribute = objectType.TemplateAttributes.SingleOrDefault(ta =>
            ta.MetaverseAttribute != null &&
            ta.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager);

        if (objectType.MetaverseObjectType.Name != Constants.BuiltInObjectTypes.User || templateManagerAttribute is not { MetaverseAttribute: not null, ManagerDepthPercentage: not null })
            return;
            
        // binary tree approach:
        // - project users to new list
        // - create manager list and remove manager from user list
        // - create binary tree using managers
        // - navigate binary tree and assign manager attributes to user objects
        // - then work out how to gradually assign more subordinates from the non-managers list as you go deeper into the tree

        if (templateManagerAttribute == null)
            return;

        if (templateManagerAttribute.ManagerDepthPercentage == null)
            return;

        if (templateManagerAttribute.MetaverseAttribute == null)
            return;

        var users = metaverseObjectsToCreate.Where(mo => mo.Type == objectType.MetaverseObjectType).ToList();
        var managerTreePrepStopwatch = Stopwatch.StartNew();
        var managerAttribute = templateManagerAttribute.MetaverseAttribute;
        var managersNeeded = users.Count * templateManagerAttribute.ManagerDepthPercentage.Value / 100;

        // randomly select managers and remove them from the users list so we have a list of managers and a list of potential direct reports
        var managers = new List<MetaverseObject>();
        for (var i = 0; i < managersNeeded; i++)
        {
            var userIndex = random.Next(0, users.Count);
            managers.Add(users[userIndex]);
            users.RemoveAt(userIndex);
        }

        // we've now got a list of managers, and we've got a list of users who are not managers, and can become non-manager subordinates
        var managerTree = new BinaryTree(managers);
        managerTreePrepStopwatch.Stop();
        var managerTreeNodeCount = 0;
        RecursivelyCountBinaryTreeNodes(managerTree, ref managerTreeNodeCount);
        Log.Verbose($"ExecuteTemplateAsync: Manager tree node count: {managerTreeNodeCount:N0}");
        Log.Verbose($"ExecuteTemplateAsync: Manager tree prep took: {managerTreePrepStopwatch.Elapsed}");

        // navigate the binary tree and assign manager attributes
        var assignManagersStopwatch = Stopwatch.StartNew();
        RecursivelyAssignUserManagers(managerTree, templateManagerAttribute.MetaverseAttribute);

        // do the same for non-manager subordinates, i.e. assign everyone else a manager
        var subordinatesAssigned = 0;
        var subordinatesToAssign = managerTreeNodeCount > 1 ? users.Count / (managerTreeNodeCount - 1) : users.Count;
        RecursivelyAssignSubordinates(managerTree, subordinatesToAssign, users, isFirstNode: true, templateManagerAttribute.MetaverseAttribute, ref subordinatesAssigned);
        Log.Verbose($"ExecuteTemplateAsync: Assigned {subordinatesAssigned:N0} subordinates a manager");

        managerTree = null;
        assignManagersStopwatch.Stop();
        Log.Verbose($"ExecuteTemplateAsync: Assigning managers to binary tree took: {assignManagersStopwatch.Elapsed}");
    }

    private static void RemoveUnnecessaryAttributeValues(ExampleDataTemplate dataGenerationTemplate, IReadOnlyCollection<MetaverseObject> metaverseObjects, Random random)
    {
        Log.Verbose("RemoveUnnecessaryAttributeValues: Started...");
        var stopwatch = Stopwatch.StartNew();
        var attributeValuesRemoved = 0;
        foreach (var dataGenerationObjectType in dataGenerationTemplate.ObjectTypes)
        {
            // find all data generation template attributes that have a population percentage less than 100%
            // that we need to reduce the number of assignments down for

            var metaverseObjectsOfType = metaverseObjects.Where(m => m.Type == dataGenerationObjectType.MetaverseObjectType).ToList();
            foreach (var dataGenAttributeToReduce in dataGenerationObjectType.TemplateAttributes.Where(q => q.PopulatedValuesPercentage < 100))
            {
                // determine how many attributes we have
                // determine how many we need to eliminate
                // randomly clear that many from the Metaverse Objects

                var percentage = dataGenAttributeToReduce.PopulatedValuesPercentage ?? 100;
                var needToRemove = metaverseObjectsOfType.Count * (100 - percentage) / 100;
                for (var i = 0; i < needToRemove; i++)
                {
                    var indexToRemove = random.Next(0, metaverseObjectsOfType.Count);
                    metaverseObjectsOfType[indexToRemove].AttributeValues.RemoveAll(q => q.Attribute == dataGenAttributeToReduce.MetaverseAttribute);
                    attributeValuesRemoved++;
                }
            }
        }
        stopwatch.Stop();
        Log.Verbose($"RemoveUnnecessaryAttributeValues: Removed {attributeValuesRemoved:N0} attribute values. Took {stopwatch.Elapsed} to complete");
    }
    #endregion

    #region Attribute Value Generation
    private static string ReplaceAttributeVariables(MetaverseObject metaverseObject, string textToProcess)
    {
        // match attribute variables (that do not contain numbers)
        // enumerate, find their value and replace
        var regex = new Regex(@"({.*?[^\d]})", RegexOptions.Compiled);
        foreach (var match in regex.Matches(textToProcess).Cast<Match>())
        {
            // snip off the brackets: {} to get the attribute name, i.e FirstName
            var attributeName = match.Value[1..^1];

            // find the attribute value on the Metaverse Object:
            var attribute = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Name == attributeName) ?? throw new InvalidDataException($"AttributeValue not found for Attribute: {attributeName}. Check your pattern. Check that you have added the ExampleDataTemplateAttribute before the pattern is defined.");
            textToProcess = textToProcess.Replace(match.Value, attribute.StringValue);
        }

        return textToProcess;
    }

    private static string ReplaceSystemVariables(
        MetaverseObject metaverseObject,
        MetaverseAttribute metaverseAttribute,
        ExampleDataValueTrackerStore trackerStore,
        string textToProcess)
    {
        // match system variables
        // enumerate, process
        var regex = new Regex(@"(\[.*?\])", RegexOptions.Compiled);
        var systemVars = regex.Matches(textToProcess);
        foreach (var match in systemVars.Cast<Match>())
        {
            // snip off the brackets: {} to get the attribute name, i.e FirstName
            var variableName = match.Value[1..^1];

            // keeping these as strings for now. They will need evolving into part of the Functions feature at some point
            if (variableName != "UniqueInt")
                continue;

            // is the string value unique amongst all MetaverseObjects of the same type?
            // if so, replace the system variable with an empty string
            // if not, add a uniqueness in in place of the system variable

            // get the text value without any unique int added, i.e. "joe.bloggs@demo.tetron.io"
            var textWithoutSystemVar = textToProcess.Replace(match.Value, string.Empty);

            // ask the tracker how many times this base value has been generated for this object type and attribute.
            // 1 means it is unique so far, so we emit it without a unique int. 2, 3, ... means we have generated it
            // before, so we append that integer to disambiguate. This is atomic, so concurrent generation of the same
            // base value still yields distinct suffixes.
            var occurrence = trackerStore.NextUniqueIntSuffix(metaverseObject.Type.Id, metaverseAttribute.Id, textWithoutSystemVar);
            textToProcess = occurrence == 1
                ? textWithoutSystemVar
                : textToProcess.Replace(match.Value, occurrence.ToString());
        }

        return textToProcess;
    }

    private static string ReplaceExampleDataSetVariables(
        MetaverseObject metaverseObject,
        MetaverseAttribute metaverseAttribute,
        List<ExampleDataSetInstance> exampleDataSetInstances,
        ExampleDataValueTrackerStore trackerStore,
        Random random,
        string textToProcess)
    {
        // logic:
        // - replace each example data set variable in the pattern with a random value from example data sets, populating a new value variable
        // - check if the new value variable value is unique via the tracked values list
        // - if not, re-run until it is unique

        // match example data set variables i.e. {0}
        // enumerate, process

        if (exampleDataSetInstances == null || exampleDataSetInstances.Count == 0)
            return textToProcess;

        var regex = new Regex(@"({\d.*?})", RegexOptions.Compiled);
        var exampleDataSetVariables = regex.Matches(textToProcess);

        if (exampleDataSetVariables.Count == 0)
            return textToProcess;

        var isGeneratedValueUnique = false;
        while (!isGeneratedValueUnique)
        {
            var completeGeneratedValue = textToProcess;
            foreach (Match match in exampleDataSetVariables.Cast<Match>())
            {
                // snip off the brackets: {} to get the variable, then test if it's an ExampleDataSet index, i.e. {0}
                var variable = match.Value[1..^1];
                var exampleDataSetIndex = int.Parse(variable);

                if (exampleDataSetIndex >= exampleDataSetInstances.Count)
                    throw new InvalidDataException("ExampleDataTemplateAttribute example data set index variable is too high. Smaller number needed. Must be within the bounds of the assigned ExampleDataSets");

                // get the example data set and then choose a random value from it before replacing the variable
                var exampleDataSet = exampleDataSetInstances[exampleDataSetIndex].ExampleDataSet;
                var randomValueIndex = random.Next(0, exampleDataSet.Values.Count);
                var randomValue = exampleDataSet.Values[randomValueIndex].StringValue;

                if (string.IsNullOrEmpty(randomValue))
                    throw new InvalidDataException("Did not get a string ExampleDataSetValue value from the randomly selected list of values.");

                // replace the example data set variable with the random value
                completeGeneratedValue = completeGeneratedValue.Replace(match.Value, randomValue);
            }

            // atomically reserve the generated value for this object type and attribute. if it was already used,
            // TryReserveValue returns false and we loop round to generate another candidate; otherwise it is now
            // reserved and we keep it. The check-and-reserve is atomic, so two threads that independently generate the
            // same value cannot both keep it.
            if (trackerStore.TryReserveValue(metaverseObject.Type.Id, metaverseAttribute.Id, completeGeneratedValue))
            {
                // generated value is unique
                isGeneratedValueUnique = true;
                textToProcess = completeGeneratedValue;
            }
            // else: this is not a unique value, we've generated it before. go round again until it is unique.
        }

        return textToProcess;
    }

    private static int GenerateNumberValue(MetaverseObjectType metaverseObjectType, ExampleDataTemplateAttribute dataGenTemplateAttribute, Random random, ExampleDataValueTrackerStore trackerStore)
    {
        var value = 1;
        int attributeId;
        if (dataGenTemplateAttribute.MetaverseAttribute != null)
            attributeId = dataGenTemplateAttribute.MetaverseAttribute.Id;
        else
            throw new InvalidDataException("Only supporting MetaverseObjects for now");

        if (dataGenTemplateAttribute.RandomNumbers.HasValue && dataGenTemplateAttribute.RandomNumbers.Value)
        {
            // random numbers
            if (dataGenTemplateAttribute is { MinNumber: not null, MaxNumber: null })
            {
                // min value only
                value = random.Next(dataGenTemplateAttribute.MinNumber.Value, int.MaxValue);
            }
            else if (!dataGenTemplateAttribute.MinNumber.HasValue && dataGenTemplateAttribute.MaxNumber.HasValue)
            {
                // max value only
                value = random.Next(dataGenTemplateAttribute.MaxNumber.Value);
            }
            else if (dataGenTemplateAttribute is { MinNumber: not null, MaxNumber: not null })
            {
                // min and max values
                value = random.Next(dataGenTemplateAttribute.MinNumber.Value, dataGenTemplateAttribute.MaxNumber.Value);
            }
        }
        else
        {
            // sequential numbers: the first value generated for this object type and attribute is the configured
            // minimum (or 1 if unset), and each subsequent value is one higher. The store assigns these atomically
            // with an O(1) keyed lookup, so the parallel generation loop is not serialised on a single lock.
            var seed = dataGenTemplateAttribute.MinNumber ?? 1;
            value = trackerStore.NextSequential(metaverseObjectType.Id, attributeId, seed);
        }

        return value;
    }
    #endregion

    #region Manager Assignment
    private static void RecursivelyCountBinaryTreeNodes(BinaryTree binaryTree, ref int nodeCount)
    {
        nodeCount++;
        if (binaryTree.Left != null)
            RecursivelyCountBinaryTreeNodes(binaryTree.Left, ref nodeCount);

        if (binaryTree.Right != null)
            RecursivelyCountBinaryTreeNodes(binaryTree.Right, ref nodeCount);
    }

    private static void RecursivelyAssignUserManagers(BinaryTree binaryTree, MetaverseAttribute managerAttribute)
    {
        // binaryTree.MetaverseObject is the manager
        // assign this in a manager attribute to both the left and right branches

        if (binaryTree.Left != null)
        {
            binaryTree.Left.MetaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = managerAttribute,
                ReferenceValue = binaryTree.MetaverseObject
            });

            RecursivelyAssignUserManagers(binaryTree.Left, managerAttribute);
        }

        if (binaryTree.Right == null) 
            return;
            
        binaryTree.Right.MetaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = managerAttribute,
            ReferenceValue = binaryTree.MetaverseObject
        });

        RecursivelyAssignUserManagers(binaryTree.Right, managerAttribute);
    }

    private static void RecursivelyAssignSubordinates(BinaryTree binaryTree, int subordinatesToAssign, List<MetaverseObject> users, bool isFirstNode, MetaverseAttribute managerAttribute, ref int subordinatesAssigned)
    {
        if (isFirstNode)
        {
            // don't assign any subordinates. the top manager can just have managers as subordinates
            isFirstNode = false;
        }
        else
        {
            // take the required number of subordinates out of the user list and assign them as subordinates to this manager
            var availableSubordinates = users.Count >= subordinatesToAssign ? subordinatesToAssign : users.Count;
            var subordinates = new MetaverseObject[availableSubordinates];
            for (var i = 0; i < availableSubordinates; i++)
                subordinates[i] = users[i];
            users.RemoveRange(0, subordinates.Length);

            foreach (var user in subordinates)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = managerAttribute,
                    ReferenceValue = binaryTree.MetaverseObject
                });

                subordinatesAssigned++;
            }
        }

        // now recurse and do the same for the left and right branches, if they exist
        if (binaryTree.Left != null)
            RecursivelyAssignSubordinates(binaryTree.Left, subordinatesToAssign, users, isFirstNode, managerAttribute, ref subordinatesAssigned);

        if (binaryTree.Right != null)
            RecursivelyAssignSubordinates(binaryTree.Right, subordinatesToAssign, users, isFirstNode, managerAttribute, ref subordinatesAssigned);
    }
    #endregion
}