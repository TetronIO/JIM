// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Run Profiles tab's Preview action (#1530). Offered on Full Synchronisation Run Profiles only: the preview walks
/// the whole population as a Full Synchronisation does, so it says nothing true about an import, an export or a Delta
/// Synchronisation, which process other things.
/// </summary>
[TestFixture]
public class ConnectedSystemRunProfilesTabTests : JimComponentTestContext
{
    private const string PreviewMarker = "jim-run-profile-preview";

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory());
        Services.AddSingleton<IUserPreferenceService>(new Mock<IUserPreferenceService>().Object);
    }

    [Test]
    public void RunProfilesTab_PreviewAction_IsOfferedOnFullSynchronisationRunProfilesOnly()
    {
        ConnectedSystemRunProfile? requested = null;

        var cut = Render<ConnectedSystemRunProfilesTab>(p => p
            .Add(x => x.ConnectedSystem, System())
            .Add(x => x.OnPreviewRequested, profile => requested = profile));
        cut.WaitForState(() => cut.Markup.Contains("Delta Synchronisation"), TimeSpan.FromSeconds(2));

        var previews = cut.FindAll($"[data-testid='{PreviewMarker}']");
        Assert.That(previews, Has.Count.EqualTo(1), "one Full Synchronisation Run Profile among four");
        previews[0].Click();
        Assert.That(requested?.RunType, Is.EqualTo(ConnectedSystemRunType.FullSynchronisation));
    }

    private static ConnectedSystem System() => new()
    {
        Id = 7,
        Name = "Yellowstone HR",
        ConnectorDefinition = new ConnectorDefinition { Name = "CSV" },
        RunProfiles =
        [
            new ConnectedSystemRunProfile { Id = 1, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = 7 },
            new ConnectedSystemRunProfile { Id = 2, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation, ConnectedSystemId = 7 },
            new ConnectedSystemRunProfile { Id = 3, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation, ConnectedSystemId = 7 },
            new ConnectedSystemRunProfile { Id = 4, Name = "Export", RunType = ConnectedSystemRunType.Export, ConnectedSystemId = 7 }
        ]
    };

    private sealed class FakeJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() => new(new Mock<IRepository>().Object);
    }
}
