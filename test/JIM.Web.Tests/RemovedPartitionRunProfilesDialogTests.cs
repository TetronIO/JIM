// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The choice the Scope tab's Retrieve Hierarchy asks for when the refresh would remove a partition that Run Profiles
/// target (#2740). The refresh has saved nothing when it opens, so what matters is that the administrator sees every
/// Run Profile and the partition each targets, and that only Disable Run Profiles goes ahead.
/// </summary>
[TestFixture]
public class RemovedPartitionRunProfilesDialogTests : JimComponentTestContext
{
    private const string TableMarker = "jim-removed-partition-run-profiles";
    private const string DisableMarker = "jim-removed-partition-run-profiles-disable";
    private const string CancelMarker = "jim-removed-partition-run-profiles-cancel";

    private (IRenderedComponent<MudDialogProvider> Provider, Task<bool> Result) Show(params HierarchyRefreshRunProfileItem[] runProfiles)
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        Task<bool> result = null!;
        provider.InvokeAsync(() => { result = RemovedPartitionRunProfilesDialog.ShowAsync(dialogService, runProfiles); });
        provider.WaitForElement($"[data-testid='{DisableMarker}']");
        return (provider, result);
    }

    private static HierarchyRefreshRunProfileItem Item(int id, string name, string partition) =>
        new() { RunProfileId = id, RunProfileName = name, PartitionName = partition };

    [Test]
    public void RemovedPartitionRunProfilesDialog_ListsEachRunProfileAndThePartitionItTargets()
    {
        var (provider, _) = Show(
            Item(7, "Full Import (Glitterband)", "dc=glitterband,dc=local"),
            Item(8, "Delta Import (Glitterband)", "dc=glitterband,dc=local"));

        var rows = provider.Find($"[data-testid='{TableMarker}'] tbody").QuerySelectorAll("tr");
        Assert.That(rows.Select(r => r.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray()),
            Is.EqualTo(new[]
            {
                new[] { "Full Import (Glitterband)", "dc=glitterband,dc=local" },
                new[] { "Delta Import (Glitterband)", "dc=glitterband,dc=local" }
            }));
    }

    [Test]
    public async Task RemovedPartitionRunProfilesDialog_DisableRunProfiles_GoesAheadAsync()
    {
        var (provider, result) = Show(Item(7, "Full Import (Glitterband)", "dc=glitterband,dc=local"));

        await provider.Find($"[data-testid='{DisableMarker}']").ClickAsync();

        Assert.That(await result, Is.True);
    }

    [Test]
    public async Task RemovedPartitionRunProfilesDialog_Cancel_DoesNotGoAheadAsync()
    {
        var (provider, result) = Show(Item(7, "Full Import (Glitterband)", "dc=glitterband,dc=local"));

        await provider.Find($"[data-testid='{CancelMarker}']").ClickAsync();

        Assert.That(await result, Is.False);
    }
}
