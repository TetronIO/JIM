// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// The password channel against a domain controller with Windows Server 2025's defaults (#1853): LDAP signing is
/// enforced, so a simple bind over plain LDAP is refused, and <c>unicodePwd</c> is only writable over an encrypted
/// connection. The Samba lab switches the equivalent setting off (<c>ldap server require strong auth = no</c>).
/// </summary>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectoryPasswordProbeTests
{
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public async Task SetPasswordAsync_OverLdaps_SetsThePasswordAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        string userDn;
        using (var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger))
            userDn = ActiveDirectoryLab.EnsureUser(admin, lab, "JIM Probe Password User", "jim-probe-password");

        using var connector = ActiveDirectoryLab.NewConnector(lab);
        connector.OpenPasswordConnection(ActiveDirectoryLab.ConnectorSettings(lab));
        PasswordSetResult result;
        try
        {
            // Satisfies the default domain policy: three character classes, well over the minimum length.
            result = await connector.SetPasswordAsync(TargetAt(userDn), $"Probe-{Guid.NewGuid():N}-Aa1!", new PasswordSetOptions(), CancellationToken.None);
        }
        finally
        {
            connector.ClosePasswordConnection();
        }

        Assert.That(result.Success, Is.True, $"{result.FailureReason}: {result.ErrorMessage}");
    }

    [Test]
    public void OpenPasswordConnection_OverPlainLdapToASigningEnforcingDomainController_IsRefusedWithAnActionableMessage()
    {
        var lab = ActiveDirectoryLab.Require();
        if (lab.PlainPort == null)
            Assert.Ignore("JIM_TEST_AD_PLAIN_PORT not set; skipping the plain LDAP refusal probe.");

        using var connector = ActiveDirectoryLab.NewConnector(lab);
        var settings = ActiveDirectoryLab.ConnectorSettings(lab, useSecureConnection: false, port: lab.PlainPort);

        // Windows Server 2025 enforces LDAP signing by default, which refuses a simple bind over an unencrypted
        // connection (strongerAuthRequired). JIM cannot make that bind succeed; what it owes the administrator is a
        // message that names the remedy rather than a bare protocol error.
        var exception = Assert.Catch<Exception>(() => connector.OpenPasswordConnection(settings), "The domain controller must refuse a simple bind over plain LDAP.")
            ?? throw new InvalidOperationException("Assert.Throws returned no exception.");
        TestContext.Out.WriteLine($"Refused with: {exception.GetType().Name}: {exception.Message}");
        Assert.That(exception.Message, Does.Contain("LDAPS").IgnoreCase.Or.Contain("sign").IgnoreCase.Or.Contain("encrypt").IgnoreCase,
            "The refusal must tell the administrator to use LDAPS (or that the directory requires signing).");
    }

    private static ConnectedSystemObject TargetAt(string dn)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "user" };
        var dnAttribute = ActiveDirectoryLab.Attribute(objectType, 1, "distinguishedName", AttributeDataType.Text, isSecondaryExternalId: true);
        objectType.Attributes.Add(dnAttribute);
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = objectType,
            SecondaryExternalIdAttributeId = dnAttribute.Id,
            AttributeValues = [new ConnectedSystemObjectAttributeValue { AttributeId = dnAttribute.Id, StringValue = dn }]
        };
    }
}
