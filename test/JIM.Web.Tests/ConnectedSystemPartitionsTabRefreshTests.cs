// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Refreshing the hierarchy on the Partitions and Containers tab while the selection holds unsaved edits (#2743).
/// </summary>
/// <remarks>
/// The refresh saves the hierarchy it reads and the page then reloads the Connected System, so whatever the
/// administrator had ticked but not saved is gone afterwards. It used to be saved along with the refresh instead,
/// past the confirmation that deselecting a partition asks for. Neither may happen without the administrator choosing
/// it, so a refresh over unsaved edits asks first.
/// </remarks>
[TestFixture]
public class ConnectedSystemPartitionsTabRefreshTests : JimComponentTestContext
{
    private const string DiscardAndRefresh = "Discard and Refresh";
    private readonly RecordingJimApplicationFactory _jimFactory = new();

    public ConnectedSystemPartitionsTabRefreshTests()
    {
        // Registered in the constructor rather than a SetUp: bUnit builds its service provider on the first
        // render and refuses registrations after that.
        Services.AddSingleton<IJimApplicationFactory>(_jimFactory);
        Services.AddSingleton<IConfigurationChangePreviewStarter>(new UnusedPreviewStarter());
    }

    [Test]
    public async Task Refresh_WithUnsavedEdits_AsksBeforeDiscardingThemAsync()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render(ConnectedSystemWithHierarchy());
        await EditSelectionAsync(cut);

        var refresh = StartRefresh(cut);
        provider.WaitForElement(".mud-message-box");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("discards your unsaved changes"),
                "the administrator is about to lose their edits, so the question has to say so");
            Assert.That(_jimFactory.Created, Is.EqualTo(0), "nothing is refreshed until the administrator answers");
        }

        await AnswerAsync(provider, "Cancel", refresh);
    }

    [Test]
    public async Task Refresh_WithUnsavedEditsCancelled_KeepsTheEditsAndRefreshesNothingAsync()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render(ConnectedSystemWithHierarchy());
        await EditSelectionAsync(cut);
        var refresh = StartRefresh(cut);
        provider.WaitForElement(".mud-message-box");

        await AnswerAsync(provider, "Cancel", refresh);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_jimFactory.Created, Is.EqualTo(0));
            Assert.That(cut.Find("[data-testid='jim-scope-pending']").TextContent, Does.Contain("Unsaved changes"),
                "cancelling leaves the edits exactly as they were");
        }
    }

    [Test]
    public async Task Refresh_WithUnsavedEditsDiscarded_RefreshesAsync()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render(ConnectedSystemWithHierarchy());
        await EditSelectionAsync(cut);
        var refresh = StartRefresh(cut);
        provider.WaitForElement(".mud-message-box");

        // The factory refuses to build an application, so the refresh stops at the point it reaches one; reaching it
        // is what this test is about.
        await AnswerAsync(provider, DiscardAndRefresh, refresh);

        Assert.That(_jimFactory.Created, Is.EqualTo(1));
    }

    [Test]
    public async Task Refresh_WithNoUnsavedEdits_RefreshesWithoutAskingAsync()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render(ConnectedSystemWithHierarchy());

        await AwaitRefreshAsync(() => StartRefresh(cut));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Not.Contain(DiscardAndRefresh), "there is nothing to discard, so nothing to ask");
            Assert.That(_jimFactory.Created, Is.EqualTo(1));
        }
    }

    #region Helpers

    /// <summary>
    /// Counts the applications the tab asks for, then refuses: a refresh that reaches the application layer has
    /// decided to go ahead, which is all these tests need to know, and nothing here can run one for real.
    /// </summary>
    private sealed class RecordingJimApplicationFactory : IJimApplicationFactory
    {
        public int Created { get; private set; }

        public JimApplication Create()
        {
            Created++;
            throw new RefreshReachedTheApplicationException();
        }
    }

    private sealed class RefreshReachedTheApplicationException() : Exception("The refresh reached the application layer.");

    /// <summary>
    /// Nothing here previews; the tab injects the starter, so it has to be resolvable.
    /// </summary>
    private sealed class UnusedPreviewStarter : IConfigurationChangePreviewStarter
    {
        public Task<Guid?> StartAsync(ConfigurationChangePreviewRequest request) =>
            throw new InvalidOperationException("The Partitions tab started a preview while only refreshing the hierarchy.");
    }

    private IRenderedComponent<ConnectedSystemPartitionsTab> Render(ConnectedSystem connectedSystem) =>
        Render<ConnectedSystemPartitionsTab>(parameters => parameters
            .Add(p => p.ConnectedSystem, connectedSystem)
            .Add(p => p.PartitionsAndHierarchiesText, "partitions and containers")
            .Add(p => p.PartitionAndHierarchyText, "partition and container"));

    /// <summary>
    /// Clicks Refresh without waiting for the refresh to finish: where it asks first, it finishes only once the
    /// question is answered.
    /// </summary>
    private static Task StartRefresh(IRenderedComponent<ConnectedSystemPartitionsTab> cut)
    {
        try
        {
            return cut.Find("[data-testid='jim-hierarchy-refresh']").ClickAsync();
        }
        catch (RefreshReachedTheApplicationException)
        {
            // A refresh that asks nothing reaches the application before the click returns.
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Answers the question Refresh asked with the button named <paramref name="answer"/>, then waits for the refresh.
    /// </summary>
    private static async Task AnswerAsync(IRenderedComponent<MudDialogProvider> provider, string answer, Task refresh)
    {
        // Answering resumes the refresh, and bUnit raises a handler's failure from the next event it dispatches, so a
        // refresh that goes ahead can surface its refusal from this click as well as from its own.
        await AwaitRefreshAsync(() => provider.FindAll(".mud-message-box button").Single(button => button.TextContent.Trim() == answer).ClickAsync());
        await AwaitRefreshAsync(() => refresh);
    }

    /// <summary>
    /// Waits for a refresh. One that goes ahead stops where the factory refuses to build an application; that refusal
    /// is swallowed here, and the factory's count says whether it was reached.
    /// </summary>
    private static async Task AwaitRefreshAsync(Func<Task> refresh)
    {
        try
        {
            await refresh();
        }
        catch (RefreshReachedTheApplicationException)
        {
            // Expected when the refresh goes ahead: see RecordingJimApplicationFactory.
        }
    }

    /// <summary>
    /// Edits the selection the way an administrator would, through Advanced Mode's text, leaving it unsaved.
    /// </summary>
    private static async Task EditSelectionAsync(IRenderedComponent<ConnectedSystemPartitionsTab> cut)
    {
        await cut.Find("[data-testid='jim-scope-mode-advanced']").ClickAsync();
        await cut.Find("textarea[data-testid='jim-scope-text']").InputAsync("include OU=Corp,DC=example,DC=com");
        await cut.Find("[data-testid='jim-scope-text-apply']").ClickAsync();
        Assert.That(cut.Find("[data-testid='jim-scope-pending']").TextContent, Does.Contain("Unsaved changes"),
            "the test needs an unsaved edit to start from");
    }

    /// <summary>
    /// DC=example,DC=com holding OU=Corp, retrieved already, so the tab offers Refresh.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithHierarchy()
    {
        var corp = new ConnectedSystemContainer { Id = 21, Name = "Corp", ExternalId = "OU=Corp,DC=example,DC=com" };
        var partition = new ConnectedSystemPartition
        {
            Id = 11, Name = "DC=example,DC=com", Selected = true, Containers = [corp]
        };
        corp.Partition = partition;

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            Partitions = [partition],
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1, Name = "JIM LDAP Connector", SupportsPartitions = true, SupportsPartitionContainers = true
            }
        };
    }

    #endregion
}
