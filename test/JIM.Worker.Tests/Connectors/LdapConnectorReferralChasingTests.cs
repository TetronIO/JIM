// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the connector opting out of platform referral chasing. JIM chasing referrals itself, authenticated
/// and reported, is issue #1352.
/// <para>
/// The platform follows a referral on a brand new connection that carries none of the credentials JIM bound
/// with, so a chased referral is an anonymous read. On a directory that refuses anonymous reads (the default
/// for Active Directory) every chased referral fails, and the failure surfaces against the search that
/// triggered it rather than against the referral, which reads as an authentication or network fault on a
/// connection that is in fact perfectly healthy.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorReferralChasingTests
{
    private static LdapConnection NewUnboundConnection() =>
        new(new LdapDirectoryIdentifier("directory.example.local", 389));

    [Test]
    public void DisableReferralChasing_OnANewConnection_TurnsReferralChasingOff()
    {
        // Arrange
        using var connection = NewUnboundConnection();

        // Act
        LdapConnectorUtilities.DisableReferralChasing(connection, Log.Logger);

        // Assert
        Assert.That(connection.SessionOptions.ReferralChasing, Is.EqualTo(ReferralChasingOptions.None));
    }

    [Test]
    public void DisableReferralChasing_IsNotThePlatformDefault_SoTheCallIsLoadBearing()
    {
        // Arrange: a connection JIM has not configured, standing in for the connector before the fix.
        using var connection = NewUnboundConnection();

        // Act
        var platformDefault = connection.SessionOptions.ReferralChasing;

        // Assert: if this ever becomes None of its own accord the call above is redundant, but until then
        // removing it silently restores the anonymous-chase behaviour this guards against.
        Assert.That(platformDefault, Is.Not.EqualTo(ReferralChasingOptions.None));
    }

    [Test]
    public void DisableReferralChasing_WhenTheOptionCannotBeSet_DoesNotThrow()
    {
        // Arrange: a disposed connection stands in for a platform that refuses the option. JIM must not fail
        // a run over a hardening step, so this may only ever log.
        var connection = NewUnboundConnection();
        connection.Dispose();

        // Act + Assert
        Assert.That(() => LdapConnectorUtilities.DisableReferralChasing(connection, Log.Logger), Throws.Nothing);
    }
}
