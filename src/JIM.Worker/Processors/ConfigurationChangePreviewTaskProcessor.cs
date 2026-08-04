// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Preview;
using JIM.Models.Tasking;
using System.Text.Json;

namespace JIM.Worker.Processors;

/// <summary>
/// Runs a configuration change preview that was too large to evaluate in the requesting host's process (#827).
///
/// There is deliberately almost nothing here. The orchestration, the staging, the grouping, the capping and the
/// failure handling all live in <see cref="JIM.Application.Servers.ConfigurationChangePreviewServer"/> and run
/// identically wherever the preview executes; if this class grew logic of its own, the two dispatch paths would
/// start producing different answers to the same question, and only the large previews would be wrong.
/// </summary>
public static class ConfigurationChangePreviewTaskProcessor
{
    public static async Task ProcessAsync(JimApplication jim, ConfigurationChangePreviewWorkerTask workerTask,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jim);
        ArgumentNullException.ThrowIfNull(workerTask);

        var request = RehydrateRequest(jim, workerTask);
        await jim.ConfigurationChangePreviews.RunPreviewAsync(workerTask.Activity.Id, request, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the request from the queued payload. The proposal's type comes from the adapter registered for the
    /// task's surface and never from the payload itself, so what is deserialised is always what that surface
    /// expects, whatever the row happens to contain.
    /// </summary>
    private static ConfigurationChangePreviewRequest RehydrateRequest(JimApplication jim, ConfigurationChangePreviewWorkerTask workerTask)
    {
        var proposalType = jim.ConfigurationChangePreviews.GetProposalType(workerTask.Surface);
        var proposal = JsonSerializer.Deserialize(workerTask.ProposedConfigurationPayload, proposalType)
                       ?? throw new InvalidOperationException(
                           $"The proposed configuration for preview {workerTask.Activity.Id} could not be read back as " +
                           $"{proposalType.Name}. The preview cannot run against a proposal it cannot reconstruct.");

        return new ConfigurationChangePreviewRequest
        {
            Surface = workerTask.Surface,
            TargetId = workerTask.TargetId,
            TargetGuidId = workerTask.TargetGuidId,
            TargetName = workerTask.TargetName,
            ProposedConfiguration = proposal,
            InitiatedByType = workerTask.InitiatedByType,
            InitiatedById = workerTask.InitiatedById,
            InitiatedByName = workerTask.InitiatedByName
        };
    }
}
