// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Preview;

namespace JIM.Web.Services;

/// <summary>
/// Starts a configuration change preview from the portal, asking first where the answer would be large enough that
/// the administrator should decide how much of it to keep (#827, PRD scenario 5).
///
/// It exists so that decision lives in one place. Both ways of getting it wrong are quiet: prompting for a small
/// preview trains administrators to dismiss the question, so the once-a-year preview that genuinely costs something
/// is dismissed too; not prompting for a large one caps it silently, and a drill-down that is a sample without
/// saying so is how a change gets approved as safe on the strength of the rows that happened to fit.
///
/// Surfaces call this rather than <c>StartAndDispatchPreviewAsync</c> directly. Where the preview *runs* is still
/// the framework's decision; this only settles how much of it is kept.
/// </summary>
public class ConfigurationChangePreviewStarter(IJimApplicationFactory jimFactory, IPreviewDataSetSizePrompt prompt)
    : IConfigurationChangePreviewStarter
{
    private readonly IJimApplicationFactory _jimFactory = jimFactory ?? throw new ArgumentNullException(nameof(jimFactory));
    private readonly IPreviewDataSetSizePrompt _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));

    public async Task<Guid?> StartAsync(ConfigurationChangePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var jim = _jimFactory.Create();

        // Measured in delta rows rather than affected objects: an Attribute Flow preview emits several rows per
        // object, and a threshold counted in objects would leave the largest previews in the product below the line
        // that exists for them.
        var estimate = await jim.ConfigurationChangePreviews.EstimatePreviewCostAsync(request);
        var threshold = await jim.ServiceSettings.GetConfigurationChangePreviewFullDataSetPromptThresholdAsync();

        var effectiveRequest = request;
        if (estimate.EstimatedDeltaRows > threshold)
        {
            var choice = await _prompt.AskAsync(estimate.EstimatedDeltaRows);
            if (choice is null)
                return null;

            effectiveRequest = request with { DeltaPersistence = choice.Value };
        }

        var result = await jim.ConfigurationChangePreviews.StartAndDispatchPreviewAsync(effectiveRequest);
        return result.ActivityId;
    }
}
