// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Transactional;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The retired values list (Unique Value Generation, #242, Phase 6): a read-only dialog over an attribute's retired
/// values register, which a deployment with years of leavers can hold thousands of entries in. What is pinned here
/// is the wiring that fails silently rather than visibly: the window must be read for the right attribute, the search
/// must reach the server rather than filtering one loaded window, and the holder must link only while it exists.
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class RetiredGeneratedValuesDialogTests : JimComponentTestContext
{
    private const int MetaverseAttributeId = 3;
    private static readonly Guid ExistingHolderId = Guid.Parse("6a0d3a54-1c2f-4e0b-9a51-2f7f6f2a0c11");

    private readonly List<RetiredWindowRequest> _windowRequests = [];
    private List<RetiredGeneratedValueHeader> _items = [];
    private Mock<ISyncRepository> _syncRepository = null!;

    protected override void ConfigureAdditionalServices()
    {
        _syncRepository = new Mock<ISyncRepository>();
        _syncRepository
            .Setup(r => r.GetRetiredGeneratedValueHeadersRangeAsync(
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int? mvId, int? csId, string? search, int offset, int _, bool includeTotalCount) =>
            {
                _windowRequests.Add(new RetiredWindowRequest(mvId, csId, search, offset, includeTotalCount));
                var matching = string.IsNullOrEmpty(search)
                    ? _items
                    : _items.Where(i => i.Value.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
                return (matching, includeTotalCount ? matching.Count : null);
            });

        var repository = new Mock<IRepository>();
        Services.AddSingleton<IJimApplicationFactory>(
            new FakeJimApplicationFactory(new JimApplication(repository.Object, syncRepository: _syncRepository.Object)));
    }

    [SetUp]
    public void SetUp()
    {
        _items =
        [
            new RetiredGeneratedValueHeader
            {
                Id = 2,
                MetaverseAttributeId = MetaverseAttributeId,
                AttributeName = "Account Name",
                Value = "j.okafor",
                RetiredAt = DateTime.UtcNow.AddDays(-5),
                Reason = RetiredGeneratedValueReason.Superseded,
                FromObjectId = ExistingHolderId,
                FromObjectDisplayName = "Jide Okafor",
                FromObjectExists = true,
                FromObjectTypeName = "Person",
                FromObjectTypePluralName = "People"
            },
            new RetiredGeneratedValueHeader
            {
                Id = 1,
                MetaverseAttributeId = MetaverseAttributeId,
                AttributeName = "Account Name",
                Value = "marisol.fenwick1",
                RetiredAt = DateTime.UtcNow.AddDays(-3),
                Reason = RetiredGeneratedValueReason.ObjectDeleted,
                FromObjectId = Guid.NewGuid(),
                FromObjectDisplayName = "Marisol Fenwick",
                FromObjectExists = false
            }
        ];
    }

    private IRenderedComponent<MudDialogProvider> ShowDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<RetiredGeneratedValuesDialog>
        {
            { x => x.AttributeName, "Account Name" },
            { x => x.MetaverseAttributeId, MetaverseAttributeId },
            { x => x.ExpectedCount, _items.Count }
        };
        provider.InvokeAsync(() => dialogService.ShowAsync<RetiredGeneratedValuesDialog>("Retired values", parameters));
        provider.WaitForAssertion(() => Assert.That(
            provider.HasComponent<VirtualisedDataGrid<RetiredGeneratedValueHeader>>(), Is.True,
            "the list must be the shared virtualised grid: a register of thousands of leavers has to stay usable"));
        return provider;
    }

    [Test]
    public void RetiredGeneratedValuesDialog_Opens_ReadsTheFirstWindowOfThisAttributesRegisterAndCountsIt()
    {
        var provider = ShowDialog();

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_windowRequests, Is.Not.Empty);
                Assert.That(_windowRequests[0].MetaverseAttributeId, Is.EqualTo(MetaverseAttributeId));
                Assert.That(_windowRequests[0].ConnectedSystemObjectTypeAttributeId, Is.Null);
                Assert.That(_windowRequests[0].Offset, Is.Zero);
                Assert.That(_windowRequests[0].IncludeTotalCount, Is.True, "the first window must count the register");
                Assert.That(provider.Markup, Does.Contain("j.okafor"));
                Assert.That(provider.Markup, Does.Contain("marisol.fenwick1"));
            }
        });
    }

    [Test]
    public void RetiredGeneratedValuesDialog_Searched_PassesTheSearchToTheServer()
    {
        var provider = ShowDialog();
        provider.WaitForAssertion(() => Assert.That(_windowRequests, Is.Not.Empty));

        var searchField = provider.FindComponent<SearchField>();
        provider.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("fenwick")).GetAwaiter().GetResult();

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_windowRequests.Last().Search, Is.EqualTo("fenwick"),
                    "the search must reach the range read rather than filtering one loaded window");
                Assert.That(provider.Markup, Does.Not.Contain("j.okafor"));
            }
        });
    }

    [Test]
    public void RetiredGeneratedValuesDialog_HolderStillExists_LinksTheMetaverseObject()
    {
        var provider = ShowDialog();

        provider.WaitForAssertion(() =>
        {
            var chips = provider.FindComponents<ObjectChip>();
            Assert.That(chips.Select(c => c.Instance.Href),
                Is.EquivalentTo(new[] { $"/t/people/v/{ExistingHolderId}" }),
                "only the holder that still exists is linked");
        });
    }

    [Test]
    public void RetiredGeneratedValuesDialog_HolderDeleted_ShowsItsLastNameWithoutALink()
    {
        var provider = ShowDialog();

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.Markup, Does.Contain("Marisol Fenwick"));
                Assert.That(provider.FindComponents<ObjectChip>().Any(c => c.Instance.Name == "Marisol Fenwick"), Is.False);
            }
        });
    }

    private sealed record RetiredWindowRequest(
        int? MetaverseAttributeId, int? ConnectedSystemObjectTypeAttributeId, string? Search, int Offset, bool IncludeTotalCount);

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
