// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.Graph;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The Microsoft Graph Connector's password channel, through which JIM's Initial Password, Set Password and Password
/// Synchronisation reach Entra ID: <c>PATCH /users/{id}</c> with <c>passwordProfile</c>.
/// </summary>
[TestFixture]
public class GraphConnectorPasswordTests
{
    private const string Password = "Correct-Horse-Battery-9";
    private FakeGraphTenant _tenant = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _tenant = new FakeGraphTenant();
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public async Task SetPassword_RequireChange_SetsThePasswordAndForcesAChangeAtNextSignInAsync()
    {
        _tenant.AddUser("u1", "Ada");

        var result = await SetAsync("u1", new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn });

        var profile = _tenant.User("u1")!["passwordProfile"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(result.ExpiryBehaviourHonoured, Is.True);
            Assert.That((string?)profile["password"], Is.EqualTo(Password));
            Assert.That(profile["forceChangePasswordNextSignIn"]!.GetValue<bool>(), Is.True);
            Assert.That(_tenant.User("u1")!["accountEnabled"], Is.Null, "An enabled state JIM was not asked about is left alone.");
        }
    }

    [Test]
    public async Task SetPassword_ExpiresByPolicy_DoesNotForceAChangeAsync()
    {
        _tenant.AddUser("u1", "Ada");

        var result = await SetAsync("u1", new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
            Assert.That(_tenant.User("u1")!["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>(), Is.False);
        }
    }

    [Test]
    public async Task SetPassword_NeverExpires_AddsDisablePasswordExpirationKeepingOtherPoliciesAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["passwordPolicies"] = "DisableStrongPassword");

        var result = await SetAsync("u1", new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(result.PasswordNeverExpiresChanged, Is.True);
            Assert.That(((string)_tenant.User("u1")!["passwordPolicies"]!).Split(", "), Is.EquivalentTo(new[] { "DisableStrongPassword", "DisablePasswordExpiration" }));
        }
    }

    [Test]
    public async Task SetPassword_RequireChangeOnAPasswordThatNeverExpires_ClearsTheFlagAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["passwordPolicies"] = "DisablePasswordExpiration");

        var result = await SetAsync("u1", new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PasswordNeverExpiresChanged, Is.True);
            Assert.That((string?)_tenant.User("u1")!["passwordPolicies"], Is.EqualTo("None"));
        }
    }

    [Test]
    public async Task SetPassword_LeavingNeverExpiresAsIs_DoesNotTouchItAndReportsWhatTheAccountHasAsync()
    {
        // Entra ID applies a forced change whether or not the password otherwise expires, unlike Active Directory, so a
        // forced change is still honoured; a password left to policy reports that the account's never expires.
        _tenant.AddUser("u1", "Ada", u => u["passwordPolicies"] = "DisablePasswordExpiration");
        _tenant.AddUser("u2", "Grace", u => u["passwordPolicies"] = "DisablePasswordExpiration");

        var forced = await SetAsync("u1", new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn, LeavePasswordNeverExpiresAsIs = true });
        var byPolicy = await SetAsync("u2", new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, LeavePasswordNeverExpiresAsIs = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forced.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(forced.ExpiryBehaviourHonoured, Is.True);
            Assert.That(byPolicy.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(byPolicy.PasswordNeverExpiresChanged, Is.False);
            Assert.That((string?)_tenant.User("u1")!["passwordPolicies"], Is.EqualTo("DisablePasswordExpiration"));
        }
    }

    [Test]
    public async Task SetPassword_EnableAccount_EnablesItInTheSameRequestAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["accountEnabled"] = false);

        await SetAsync("u1", new PasswordSetOptions { EnableAccount = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_tenant.User("u1")!["accountEnabled"]!.GetValue<bool>(), Is.True);
            Assert.That(_tenant.Handler.Requests.Count(r => r.Method == HttpMethod.Patch), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task SetPassword_UserNotFound_IsTargetObjectNotFoundAsync()
    {
        var result = await SetAsync("gone", new PasswordSetOptions());

        Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.TargetObjectNotFound));
    }

    [Test]
    public async Task SetPassword_Refused_IsAConfigurationFaultNamingThePermissionAndRoleAsync()
    {
        _tenant.AddUser("u1", "Ada");
        _tenant.WriteInterceptor = (_, _, _) => (HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.");

        var result = await SetAsync("u1", new PasswordSetOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.ErrorMessage, Does.Contain("User-PasswordProfile.ReadWrite.All"));
            Assert.That(result.ErrorMessage, Does.Contain("User Administrator"));
        }
    }

    [Test]
    public async Task SetPassword_PasswordRefused_IsAPolicyRejectionInEntraIdsWordsAsync()
    {
        _tenant.AddUser("u1", "Ada");
        _tenant.WriteInterceptor = (_, _, _) => (HttpStatusCode.BadRequest, "Request_BadRequest",
            "The specified password does not comply with password complexity requirements. Please provide a different password.");

        var result = await SetAsync("u1", new PasswordSetOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(result.ErrorMessage, Does.Contain("password complexity requirements"));
        }
    }

    [Test]
    public async Task SetPassword_SynchronisedUser_IsUnsupportedAndSaysWhereItsPasswordLivesAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["onPremisesSyncEnabled"] = true);

        var result = await SetAsync("u1", new PasswordSetOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.UnsupportedOperation));
            Assert.That(result.ErrorMessage, Does.Contain("Active Directory"));
        }
    }

    [Test]
    public async Task SetPassword_Group_IsUnsupportedWithoutAskingGraphAsync()
    {
        var connector = Open();
        var result = await connector.SetPasswordAsync(Target("g1", "group"), Password, new PasswordSetOptions(), CancellationToken.None);
        connector.ClosePasswordConnection();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.UnsupportedOperation));
            Assert.That(_tenant.Handler.Requests.Count(r => r.Method != HttpMethod.Post), Is.Zero);
        }
    }

    [Test]
    public void Capabilities_DeclareThePasswordChannel()
    {
        var connector = new GraphConnector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.SupportsPasswordSet, Is.True);
            Assert.That(connector.SupportsPasswordPolicyDiscovery, Is.True);
            Assert.That(connector.SupportedExpiryBehaviours, Is.EquivalentTo(Enum.GetValues<PasswordExpiryBehaviour>()));
        }
    }

    [Test]
    public void PasswordChannel_IsAlwaysEncrypted()
    {
        var connector = Open();

        Assert.That(connector.IsPasswordChannelSecure, Is.True);
        connector.ClosePasswordConnection();
    }

    [Test]
    public async Task PasswordPolicy_IsEntraIdsFixedRulesWithFurtherChecksJimCannotSeeAsync()
    {
        var policy = await new StubbedTransportGraphConnector(_tenant.Handler).GetPasswordPolicyAsync(Settings(), _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy!.MinimumLength, Is.EqualTo(8));
            Assert.That(policy.ComplexityRequired, Is.True);
            Assert.That(policy.RequiredCharacterClassCount, Is.EqualTo(3));
            Assert.That(policy.RecognisedCharacterClasses, Is.EqualTo(PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase | PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol));
            Assert.That(policy.FurtherChecksApply, Is.True, "Entra ID's banned password list cannot be read.");
            Assert.That(policy.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.Read));
        }
    }

    [Test]
    public async Task Preflight_PermissionAndTenantWideUserAdministrator_IsReadyAsync()
    {
        _tenant.Roles = [.. GraphTestTokens.AllPermissions, "User-PasswordProfile.ReadWrite.All"];
        _tenant.DirectoryRoles = [GraphTestTokens.UserAdministratorRole];

        var result = await PreflightAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.Ready));
            Assert.That(Check(result, PasswordPreflightCheck.ResetRights).State, Is.EqualTo(PasswordPreflightState.Passed));
        }
    }

    [Test]
    public async Task Preflight_WithoutThePermission_FailsNamingItAsync()
    {
        _tenant.Roles = ["User.Read.All", "Group.Read.All"];

        var result = await PreflightAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.NotReady));
            Assert.That(Check(result, PasswordPreflightCheck.ResetRights).State, Is.EqualTo(PasswordPreflightState.Failed));
            Assert.That(Check(result, PasswordPreflightCheck.ResetRights).Message, Does.Contain("User-PasswordProfile.ReadWrite.All"));
        }
    }

    [Test]
    public async Task Preflight_PermissionWithoutARoleInTheToken_CannotTellSinceTheTokenMayPredateTheRoleAsync()
    {
        // The token's wids claim lists only tenant-wide roles held when it was issued, so a role's absence proves nothing.
        _tenant.Roles = ["User.Read.All", "Group.Read.All", "User-PasswordProfile.ReadWrite.All"];

        var result = await PreflightAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.Inconclusive));
            Assert.That(Check(result, PasswordPreflightCheck.ResetRights).State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine));
            Assert.That(Check(result, PasswordPreflightCheck.ResetRights).Message, Does.Contain("User Administrator"));
        }
    }

    [Test]
    public async Task Preflight_TokenRefused_FailsTheConnectionAndDeterminesNothingElseAsync()
    {
        var handler = new StubHttpMessageHandler(_ => GraphTestTokens.TokenRefusal(7000215));

        var result = await new StubbedTransportGraphConnector(handler).RunPasswordPreflightAsync(Settings(), [], _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Check(result, PasswordPreflightCheck.Connection).State, Is.EqualTo(PasswordPreflightState.Failed));
            Assert.That(Check(result, PasswordPreflightCheck.ResetRights).State, Is.EqualTo(PasswordPreflightState.CouldNotDetermine));
        }
    }

    private async Task<PasswordSetResult> SetAsync(string id, PasswordSetOptions options)
    {
        var connector = Open();
        try
        {
            return await connector.SetPasswordAsync(Target(id, "user"), Password, options, CancellationToken.None);
        }
        finally
        {
            connector.ClosePasswordConnection();
        }
    }

    private Task<PasswordPreflightResult> PreflightAsync() =>
        new StubbedTransportGraphConnector(_tenant.Handler).RunPasswordPreflightAsync(Settings(), [], _logger, CancellationToken.None);

    private StubbedTransportGraphConnector Open()
    {
        var connector = new StubbedTransportGraphConnector(_tenant.Handler);
        connector.OpenPasswordConnection(Settings());
        return connector;
    }

    private static PasswordPreflightCheckResult Check(PasswordPreflightResult result, PasswordPreflightCheck check) => result.Checks.Single(c => c.Check == check);

    private static ConnectedSystemObject Target(string id, string objectTypeName)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = objectTypeName };
        var idAttribute = new ConnectedSystemObjectTypeAttribute { Id = 99, Name = "id", IsExternalId = true, ConnectedSystemObjectType = objectType };
        return new ConnectedSystemObject
        {
            Type = objectType,
            TypeId = objectType.Id,
            ExternalIdAttributeId = idAttribute.Id,
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = idAttribute, AttributeId = idAttribute.Id, StringValue = id }]
        };
    }

    private static List<ConnectedSystemSettingValue> Settings() =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5" },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
    ];
}
