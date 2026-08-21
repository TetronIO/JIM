// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Require Secure Transport across the API layer (#1119). It lives on the Connected System because it governs
/// every password JIM sends there: the first password on an account it provisions, one an administrator sets by
/// hand, and a synchronised password change.
/// <para>
/// Mirrors the initial-password time to live coverage in
/// <see cref="ConnectedSystemInitialPasswordTimeToLiveApiTests"/>, which is the other password setting held on
/// the system rather than on any one feature's configuration.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemRequireSecureTransportApiTests
{
    [Test]
    public void FromEntity_Required_MapsCorrectly()
    {
        var entity = new ConnectedSystem { Name = "Corporate AD", RequireSecureTransport = true };

        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        Assert.That(dto.RequireSecureTransport, Is.True);
    }

    [Test]
    public void FromEntity_NotRequired_MapsCorrectly()
    {
        var entity = new ConnectedSystem { Name = "Corporate AD" };

        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        Assert.That(dto.RequireSecureTransport, Is.False, "Off unless an administrator has asked for it.");
    }

    [Test]
    public void UpdateConnectedSystemRequest_AcceptsTrue()
    {
        var request = new UpdateConnectedSystemRequest { RequireSecureTransport = true };

        Assert.That(request.RequireSecureTransport, Is.True);
    }

    [Test]
    public void UpdateConnectedSystemRequest_AcceptsFalse()
    {
        // Turning it off has to be expressible, and false is a real instruction rather than an omission; that is
        // why the request field is nullable.
        var request = new UpdateConnectedSystemRequest { RequireSecureTransport = false };

        Assert.That(request.RequireSecureTransport, Is.False);
    }

    [Test]
    public void UpdateConnectedSystemRequest_Omitted_IsNull()
    {
        var request = new UpdateConnectedSystemRequest();

        Assert.That(request.RequireSecureTransport, Is.Null,
            "An omitted field leaves the stored value alone; null is how the controller tells the two apart.");
    }

    /// <summary>
    /// The Password Synchronisation resource still reports it, because it governs that feature too, but sources
    /// it from the Connected System rather than from its own configuration row.
    /// </summary>
    [Test]
    public void PasswordSynchronisationResponse_ReportsTheConnectedSystemsSetting()
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = 3,
            Name = "Corporate AD",
            RequireSecureTransport = true,
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector", SupportsPasswordSet = true },
            PasswordSynchronisation = new ConnectedSystemPasswordSynchronisation
            {
                ConnectedSystemId = 3,
                Enabled = true,
                TargetObjectTypeId = 1
            }
        };

        var dto = ConnectedSystemPasswordSynchronisationResponse.FromEntity(connectedSystem, connectedSystem.PasswordSynchronisation);

        Assert.That(dto.RequireSecureTransport, Is.True);
    }

    /// <summary>
    /// And reports it on a system with no Password Synchronisation configuration at all, which is precisely the
    /// case the old placement could not express: such a system still provisions accounts, and still has to be
    /// able to refuse an unencrypted channel.
    /// </summary>
    [Test]
    public void PasswordSynchronisationResponse_NoConfiguration_StillReportsTheSetting()
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = 3,
            Name = "Corporate AD",
            RequireSecureTransport = true,
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector", SupportsPasswordSet = true }
        };

        var dto = ConnectedSystemPasswordSynchronisationResponse.FromEntity(connectedSystem, configuration: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Configured, Is.False);
            Assert.That(dto.RequireSecureTransport, Is.True);
        }
    }
}
