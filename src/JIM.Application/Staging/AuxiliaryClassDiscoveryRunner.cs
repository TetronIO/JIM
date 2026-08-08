// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Application.Staging;

/// <summary>
/// Runs an auxiliary class discovery: reads a Connected System's objects through the Connector, works out which
/// auxiliary classes they carry, and records the findings as suggestions.
/// </summary>
/// <remarks>
/// Nothing here changes a Connected System's schema or an administrator's selections. A discovery run only narrows
/// what the portal offers, which is why it can be run as often as an administrator likes and cancelled at any point
/// without leaving anything half-applied.
/// </remarks>
public class AuxiliaryClassDiscoveryRunner
{
    private readonly JimApplication _application;
    private readonly ILogger _logger;

    public AuxiliaryClassDiscoveryRunner(JimApplication application, ILogger logger)
    {
        _application = application;
        _logger = logger;
    }

    /// <summary>
    /// Reads every selected structural Object Type's objects and records which auxiliary classes they carry.
    /// </summary>
    /// <returns>The completed run, carrying its results and final status.</returns>
    public async Task<AuxiliaryClassDiscoveryRun> RunAsync(
        ConnectedSystem connectedSystem,
        AuxiliaryClassDiscoveryScope scope,
        int? sampleSizePerObjectType,
        Activity activity,
        IConnector connector,
        IConnectorProgress progress,
        CancellationToken cancellationToken)
    {
        if (connector is not IConnectorObjectClassUsage usageConnector)
            throw new NotSupportedException(
                $"The '{connectedSystem.ConnectorDefinition.Name}' connector cannot report which classes its objects carry, so auxiliary class discovery has nothing to read.");

        var run = new AuxiliaryClassDiscoveryRun
        {
            ConnectedSystemId = connectedSystem.Id,
            Scope = scope,
            SampleSizePerObjectType = sampleSizePerObjectType,
            Status = AuxiliaryClassDiscoveryStatus.InProgress,
            Started = DateTime.UtcNow,
            ActivityId = activity.Id,
            InitiatedById = activity.InitiatedById,
            InitiatedByName = activity.InitiatedByName
        };

        // Created before any reading starts, so the in-flight guard is in force for the whole run rather than only
        // once it finishes.
        run = await _application.ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(run);

        try
        {
            await SampleObjectTypesAsync(connectedSystem, run, usageConnector, progress, cancellationToken);

            run.Status = cancellationToken.IsCancellationRequested
                ? AuxiliaryClassDiscoveryStatus.Cancelled
                : AuxiliaryClassDiscoveryStatus.Complete;
        }
        catch (OperationCanceledException)
        {
            // A Connector is asked to stop and return what it has, but one that throws instead is still reporting
            // the administrator's own cancellation. Recording that as a failure would raise an error on the Activity
            // for something nobody needs to investigate.
            run.Status = AuxiliaryClassDiscoveryStatus.Cancelled;
            _logger.Information("AuxiliaryClassDiscoveryRunner: Discovery of '{ConnectedSystem}' was cancelled after reading {EntriesRead} objects.",
                connectedSystem.Name, run.EntriesRead);
        }
        catch (Exception ex)
        {
            // The findings gathered so far are still worth keeping: they are suggestions, and a partial set of them
            // is more use to an administrator than none.
            run.Status = AuxiliaryClassDiscoveryStatus.Failed;
            run.ErrorMessage = ex.Message;
            _logger.Error(ex, "AuxiliaryClassDiscoveryRunner: Discovery of '{ConnectedSystem}' failed after reading {EntriesRead} objects.",
                connectedSystem.Name, run.EntriesRead);
        }
        finally
        {
            run.Completed = DateTime.UtcNow;
            await _application.ConnectedSystems.UpdateAuxiliaryClassDiscoveryRunAsync(run);
        }

        return run;
    }

    private async Task SampleObjectTypesAsync(
        ConnectedSystem connectedSystem,
        AuxiliaryClassDiscoveryRun run,
        IConnectorObjectClassUsage usageConnector,
        IConnectorProgress progress,
        CancellationToken cancellationToken)
    {
        var objectTypes = connectedSystem.ObjectTypes ?? [];

        // Only the types an administrator manages. Sampling everything would read a directory's entire population
        // to answer a question about object types JIM has been told to ignore.
        var structuralObjectTypes = objectTypes
            .Where(objectType => objectType.Selected && !objectType.IsAuxiliary())
            .OrderBy(objectType => objectType.Name)
            .ToList();

        if (structuralObjectTypes.Count == 0)
        {
            _logger.Warning("AuxiliaryClassDiscoveryRunner: No Object Types are selected on '{ConnectedSystem}', so there is nothing to sample.", connectedSystem.Name);
            return;
        }

        foreach (var structuralObjectType in structuralObjectTypes)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var request = new ObjectClassUsageRequest
            {
                ObjectTypeName = structuralObjectType.Name,
                MaximumEntries = run.Scope == AuxiliaryClassDiscoveryScope.QuickSample ? run.SampleSizePerObjectType : null
            };

            var usage = await usageConnector.ReadObjectClassUsageAsync(connectedSystem, request, _logger, cancellationToken, progress);
            run.EntriesRead += usage.EntriesRead;

            var aggregation = AuxiliaryClassUsageAggregator.Aggregate(structuralObjectType, usage, objectTypes);
            foreach (var result in aggregation.Results)
            {
                result.RunId = run.Id;
                run.Results.Add(result);
            }

            if (aggregation.UnrecognisedClasses.Count > 0)
            {
                _logger.Warning("AuxiliaryClassDiscoveryRunner: '{ObjectType}' objects on '{ConnectedSystem}' carry classes the schema does not publish, so they cannot be suggested: {Classes}",
                    structuralObjectType.Name, connectedSystem.Name, string.Join(", ", aggregation.UnrecognisedClasses));
            }
        }
    }
}
