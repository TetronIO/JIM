// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using JIM.Web.Models;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the dialog listing every queued change to one multi-valued attribute of a Pending Export, now that it
/// is a <see cref="VirtualisedDataGrid{TItem}"/> over the application layer's range read. Two things are worth
/// pinning: that a window is handed to the range read exactly as it arrived, in one call and with the grid's
/// decision about counting intact (anything else shows the wrong changes, or none, with no error anywhere), and
/// that a dialog is a place the shared grid works at all, since it states its own height ceiling rather than
/// measuring the page behind the overlay.
/// </summary>
[TestFixture]
public class PendingExportMvaDialogTests : JimComponentTestContext
{
    private const string AttributeName = "member";

    private static readonly Guid PendingExportId = Guid.NewGuid();

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    /// <summary>
    /// Serves the given changes as the repository's range read does: the window at the requested offset and count,
    /// and the total only when it was asked for (null, never zero, when it was not).
    /// </summary>
    private void SetupChanges(IReadOnlyList<PendingExportAttributeValueChange> changes)
    {
        _connectedSystems
            .Setup(r => r.GetPendingExportAttributeChangesRangeAsync(PendingExportId, AttributeName,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, string _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<PendingExportAttributeValueChange>
                {
                    Results = changes.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? changes.Count : null
                });
    }

    private static List<PendingExportAttributeValueChange> BuildChanges(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = new ConnectedSystemObjectTypeAttribute { Name = AttributeName, Type = AttributeDataType.Text },
                ChangeType = PendingExportAttributeChangeType.Add,
                Status = PendingExportAttributeChangeStatus.Pending,
                StringValue = $"change-{i:D4}"
            })
            .ToList();

    private IRenderedComponent<MudDialogProvider> ShowDialog(int totalCount)
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<PendingExportMvaDialog>
        {
            { x => x.AttributeName, AttributeName },
            { x => x.PendingExportId, PendingExportId },
            { x => x.TotalCount, totalCount }
        };

        provider.InvokeAsync(() => dialogService.ShowAsync<PendingExportMvaDialog>(AttributeName, parameters));
        provider.WaitForAssertion(() =>
            Assert.That(provider.HasComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>(), Is.True));

        return provider;
    }

    private static Func<VirtualisedWindowRequest, CancellationToken, Task<VirtualisedWindow<PendingExportAttributeValueChange>>>
        LoadWindow(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>().Instance.LoadWindow;

    [Test]
    public void PendingExportMvaDialog_RendersAVirtualisedGridWithNoPager()
    {
        SetupChanges(BuildChanges(3));

        var provider = ShowDialog(totalCount: 3);

        provider.WaitForAssertion(() => Assert.That(provider.Markup, Does.Contain("change-0000")));
        Assert.That(provider.HasComponent<MudTablePager>(), Is.False,
            "a virtualised list has no page size to choose and no page controls");
    }

    [Test]
    public void PendingExportMvaDialog_KeepsTheChangeTypeStatusAndValueColumns()
    {
        SetupChanges(BuildChanges(2));

        var provider = ShowDialog(totalCount: 2);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.Markup, Does.Contain("Change Type"));
                Assert.That(provider.Markup, Does.Contain("Status"));
                Assert.That(provider.Markup, Does.Contain("Value"));
            }
        });
    }

    /// <summary>
    /// The grid addresses windows by absolute offset and count, and so does the range read behind them, so the
    /// request goes over unchanged and in a single read; the page stitching this replaced took two whenever the
    /// window did not start on a page boundary.
    /// </summary>
    [Test]
    public async Task PendingExportMvaDialog_Window_ReadsTheOffsetAndCountItWasAskedForInOneReadAsync()
    {
        var changes = BuildChanges(250);
        SetupChanges(changes);
        var provider = ShowDialog(totalCount: 250);
        _connectedSystems.Invocations.Clear();

        var window = await LoadWindow(provider)(
            new VirtualisedWindowRequest(98, 6, null, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(c => c.StringValue),
                Is.EqualTo(changes.Skip(98).Take(6).Select(c => c.StringValue)));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }

        _connectedSystems.Verify(r => r.GetPendingExportAttributeChangesRangeAsync(
            PendingExportId, AttributeName, 98, 6, null, true), Times.Once);
    }

    /// <summary>
    /// Counting is the expensive half of a window read, so a request that does not ask for it must not trigger
    /// one, and the absent total must stay null: a zero in its place reads as "nothing matched".
    /// </summary>
    [Test]
    public async Task PendingExportMvaDialog_WindowNotAskingForTheCount_DoesNotCountAndReturnsANullTotalAsync()
    {
        SetupChanges(BuildChanges(20));
        var provider = ShowDialog(totalCount: 20);
        _connectedSystems.Invocations.Clear();

        var window = await LoadWindow(provider)(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");

        _connectedSystems.Verify(r => r.GetPendingExportAttributeChangesRangeAsync(
            PendingExportId, AttributeName, 0, 5, null, false), Times.Once);
    }

    [Test]
    public void PendingExportMvaDialog_WithNoChanges_SaysSoRatherThanShowingAnEmptyTable()
    {
        SetupChanges([]);

        var provider = ShowDialog(totalCount: 0);

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindComponent<TableEmptyState>().Instance.PrimaryText,
                Is.EqualTo("No changes are queued for this attribute")));
    }

    [Test]
    public void PendingExportMvaDialog_WithChanges_ShowsNoEmptyState()
    {
        SetupChanges(BuildChanges(4));

        var provider = ShowDialog(totalCount: 4);

        provider.WaitForAssertion(() => Assert.That(provider.Markup, Does.Contain("change-0003")));
        Assert.That(provider.HasComponent<TableEmptyState>(), Is.False);
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
