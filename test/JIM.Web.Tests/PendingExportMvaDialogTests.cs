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
/// is a <see cref="VirtualisedDataGrid{TItem}"/>. Two things are worth pinning: the window arithmetic across the
/// application layer's pages (wrong arithmetic shows the wrong changes with no error anywhere), and that a
/// dialog is a place the shared grid works at all, since it sizes itself against the page rather than a
/// hand-tuned height.
/// </summary>
[TestFixture]
public class PendingExportMvaDialogTests : JimComponentTestContext
{
    private const string AttributeName = "member";
    private const int SourcePageSize = 100;

    private static readonly Guid PendingExportId = Guid.NewGuid();

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    private void SetupChanges(IReadOnlyList<PendingExportAttributeValueChange> changes)
    {
        _connectedSystems
            .Setup(r => r.GetPendingExportAttributeChangesPagedAsync(PendingExportId, AttributeName,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string _, int page, int pageSize, string? _) => new PagedResultSet<PendingExportAttributeValueChange>
            {
                Results = changes.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                TotalResults = changes.Count,
                CurrentPage = page,
                PageSize = pageSize
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

    [Test]
    public async Task PendingExportMvaDialog_WindowStraddlingAPageBoundary_IsStitchedFromBothPagesAsync()
    {
        var changes = BuildChanges(250);
        SetupChanges(changes);
        var provider = ShowDialog(totalCount: 250);

        var window = await LoadWindow(provider)(
            new VirtualisedWindowRequest(SourcePageSize - 2, 6, null, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(c => c.StringValue),
                Is.EqualTo(changes.Skip(SourcePageSize - 2).Take(6).Select(c => c.StringValue)));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }
    }

    [Test]
    public async Task PendingExportMvaDialog_WindowNotAskingForTheCount_ReturnsANullTotalAsync()
    {
        SetupChanges(BuildChanges(20));
        var provider = ShowDialog(totalCount: 20);

        var window = await LoadWindow(provider)(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");
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
