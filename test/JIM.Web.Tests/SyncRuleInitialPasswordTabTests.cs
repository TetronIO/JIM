// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Synchronisation Rule editor's Initial Password tab, on the one thing it decides rather than displays:
/// whether the initial-password settings on the rule are savable (issue #1273).
/// <para>
/// The REST API refuses settings that cannot be satisfied, because saving them parks every account the rule
/// provisions. The portal used to accept the same settings without comment, so which surface an administrator
/// used decided whether the configuration was allowed. These tests pin the portal to the same answer, and pin
/// the rule that a password the target would refuse is never taken onto the model in the first place.
/// </para>
/// </summary>
[TestFixture]
public class SyncRuleInitialPasswordTabTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const int SyncRuleId = 3;

    private JimApplication _jim = null!;

    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        var mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockSyncRepo = new Mock<ISyncRepository>();

        mockRepository.Setup(r => r.ConnectedSystems).Returns(mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.Sync).Returns(mockSyncRepo.Object);

        // The sync repository goes in explicitly: JimApplication takes what its hosts pass rather than reading
        // it off the data repository, so omitting it leaves the initial-password server without one.
        _jim = new JimApplication(mockRepository.Object, syncRepository: mockSyncRepo.Object)
        {
            // Without one, encrypting a static password would build a real Data Protection provider and write
            // keys to disk from a unit test.
            CredentialProtection = new ReversibleTestCredentialProtection()
        };

        // A target that insists on twelve characters, so that a password it would refuse is expressible without
        // reaching for one JIM would refuse on its own account.
        mockConnectedSystemRepo
            .Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId))
            .ReturnsAsync(new ConnectedSystemPasswordPolicy { MinimumLength = 12 });
        mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateFileConnectorConnectedSystem());
        mockSyncRepo
            .Setup(r => r.GetParkedInitialPasswordReasonsAsync(SyncRuleId))
            .ReturnsAsync([]);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    /// <summary>
    /// A generator configuration that cannot produce a password parks every account the rule provisions. The
    /// REST API refuses it, so the portal must report it too rather than saving it silently.
    /// </summary>
    [Test]
    public void SyncRuleInitialPasswordTab_WithAnUnsatisfiableCustomPolicy_ReportsTheProblemToItsParent()
    {
        var syncRule = CreateProvisioningSyncRule(new SyncRuleInitialPassword
        {
            SyncRuleId = SyncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Style = PasswordGenerationStyle.Words, WordCount = 0 }
        });

        var issues = RenderTabAndCaptureIssues(syncRule);

        Assert.That(issues, Is.Not.Empty);
    }

    /// <summary>
    /// Settings that can be satisfied are not reported, or the save bar would carry a permanent complaint about
    /// a perfectly good rule.
    /// </summary>
    [Test]
    public void SyncRuleInitialPasswordTab_WithSatisfiableSettings_ReportsNothing()
    {
        var syncRule = CreateProvisioningSyncRule(new SyncRuleInitialPassword
        {
            SyncRuleId = SyncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Discovered
        });

        var issues = RenderTabAndCaptureIssues(syncRule);

        Assert.That(issues, Is.Empty);
    }

    /// <summary>
    /// A rule set to give every account one password, with no password to give, is the static counterpart of an
    /// unsatisfiable generator configuration and is reported the same way.
    /// </summary>
    [Test]
    public void SyncRuleInitialPasswordTab_WithAStaticSourceAndNoPasswordSet_ReportsTheProblemToItsParent()
    {
        var syncRule = CreateProvisioningSyncRule(new SyncRuleInitialPassword
        {
            SyncRuleId = SyncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Static
        });

        var issues = RenderTabAndCaptureIssues(syncRule);

        Assert.That(issues.Any(i => i.Contains("no password has been set")), Is.True);
    }

    /// <summary>
    /// A typed password the target would refuse must not reach the model. The REST API assesses a supplied
    /// password before it stores it and refuses the request outright; the portal encrypted it first and asked
    /// afterwards, so the value was on the rule waiting for the next Save.
    /// </summary>
    [Test]
    public async Task SyncRuleInitialPasswordTab_WithAnUnusableStaticPasswordTyped_DoesNotTakeItOntoTheRuleAsync()
    {
        var configuration = new SyncRuleInitialPassword
        {
            SyncRuleId = SyncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Static
        };
        var syncRule = CreateProvisioningSyncRule(configuration);
        var issues = new List<string>();
        var cut = RenderTab(syncRule, issues);

        await TypeStaticPasswordAsync(cut, "TooShort1!");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.StaticPasswordEncryptedValue, Is.Null, "a password the target would refuse is not stored");
            Assert.That(configuration.StaticPasswordSetAt, Is.Null);
            Assert.That(issues, Is.Not.Empty, "and the administrator is told why saving is blocked");
        }
    }

    /// <summary>
    /// The other half of the rule above: a usable password is encrypted, stamped, and clears the complaint.
    /// </summary>
    [Test]
    public async Task SyncRuleInitialPasswordTab_WithAUsableStaticPasswordTyped_TakesItOntoTheRuleAsync()
    {
        var configuration = new SyncRuleInitialPassword
        {
            SyncRuleId = SyncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Static
        };
        var syncRule = CreateProvisioningSyncRule(configuration);
        var issues = new List<string>();
        var cut = RenderTab(syncRule, issues);

        await TypeStaticPasswordAsync(cut, "Correct-Horse-Battery-7");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.StaticPasswordEncryptedValue, Is.Not.Null.And.Not.EqualTo("Correct-Horse-Battery-7"),
                "the plaintext must never be what is stored");
            Assert.That(configuration.StaticPasswordSetAt, Is.Not.Null);
            Assert.That(issues, Is.Empty);
        }
    }

    #region Helper Methods

    /// <summary>
    /// Hands the typed password to the tab exactly as the password fields do when both of them
    /// agree, which is the only route by which one reaches the tab.
    /// </summary>
    private static async Task TypeStaticPasswordAsync(IRenderedComponent<SyncRuleInitialPasswordTab> cut, string password)
    {
        var section = cut.FindComponent<SyncRuleInitialPasswordSection>();
        await cut.InvokeAsync(() => section.Instance.OnStaticPasswordEntered.InvokeAsync(password));
    }

    private List<string> RenderTabAndCaptureIssues(SyncRule syncRule)
    {
        var issues = new List<string>();
        RenderTab(syncRule, issues);
        return issues;
    }

    private IRenderedComponent<SyncRuleInitialPasswordTab> RenderTab(SyncRule syncRule, List<string> issues)
    {
        return Render<SyncRuleInitialPasswordTab>(p => p
            .Add(c => c.SyncRule, syncRule)
            .Add(c => c.OnInitialPasswordIssuesChanged, reported =>
            {
                issues.Clear();
                issues.AddRange(reported);
            }));
    }

    private SyncRule CreateProvisioningSyncRule(SyncRuleInitialPassword? initialPassword) =>
        new()
        {
            Id = SyncRuleId,
            Name = "Provision Users",
            Direction = SyncRuleDirection.Export,
            ProvisionToConnectedSystem = true,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystem = CreateFileConnectorConnectedSystem(),
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" },
            InitialPassword = initialPassword
        };

    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    /// <summary>
    /// Stands in for real credential protection. Reversible and deliberately not encryption: what these tests
    /// need is a value that is not the plaintext and can be told apart from it.
    /// </summary>
    private sealed class ReversibleTestCredentialProtection : ICredentialProtectionService
    {
        private const string Prefix = "protected:";

        public string? Protect(string? plaintext) => string.IsNullOrEmpty(plaintext) ? plaintext : Prefix + plaintext;

        public string? Unprotect(string? protectedValue) =>
            IsProtected(protectedValue) ? protectedValue![Prefix.Length..] : protectedValue;

        public bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    }

    #endregion
}
