// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using System.DirectoryServices.Protocols;
using System.Reflection;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A domain controller that enforces LDAP signing (the Windows Server 2025 default) refuses a simple bind over plain
/// LDAP with strongerAuthRequired (result 8). The platform LDAP client raises that bind refusal with no response
/// attached, so the result code is not on the exception; other operations raise it with one. Either way the
/// administrator is owed a message naming the remedy rather than the bare protocol text (#2042).
/// </summary>
[TestFixture]
public class LdapBindRefusalTests
{
    // The text the platform LDAP client gives a strongerAuthRequired refusal it raises with no response attached.
    private const string PlatformStrongAuthenticationText = "Strong authentication is required for this operation.";

    [Test]
    public void Describe_StrongAuthenticationRefusalWithoutAResponse_OverPlainLdap_NamesLdapsAsTheRemedy()
    {
        var refusal = new DirectoryOperationException(null, PlatformStrongAuthenticationText);

        var described = LdapBindRefusal.Describe(refusal, useSecureConnection: false);

        Assert.That(described, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(described!.Message, Does.Contain("LDAPS"));
            Assert.That(described!.Message, Does.Contain("signing").IgnoreCase);
            Assert.That(described!.Message, Does.Contain(PlatformStrongAuthenticationText), "the directory's own words stay in the message, for searching");
            Assert.That(described!.InnerException, Is.SameAs(refusal));
        }
    }

    [Test]
    public void Describe_StrongAuthenticationRefusalWithAResponse_OverPlainLdap_NamesLdapsAsTheRemedy()
    {
        var response = CreateDirectoryResponse<ModifyResponse>(ResultCode.StrongAuthRequired);
        var refusal = new DirectoryOperationException(response, "The server returned an error.");

        var described = LdapBindRefusal.Describe(refusal, useSecureConnection: false);

        Assert.That(described, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(described!.Message, Does.Contain("LDAPS"));
            Assert.That(described!.Response, Is.SameAs(response), "the directory's response is kept for anything that reads it");
        }
    }

    [Test]
    public void Describe_StrongAuthenticationRefusalAsAnLdapException_OverPlainLdap_NamesLdapsAsTheRemedy()
    {
        var refusal = new LdapException((int)ResultCode.StrongAuthRequired, PlatformStrongAuthenticationText);

        var described = LdapBindRefusal.Describe(refusal, useSecureConnection: false);

        Assert.That(described, Is.Not.Null);
        Assert.That(described!.Message, Does.Contain("LDAPS"));
    }

    [Test]
    public void Describe_StrongAuthenticationRefusalOverLdaps_DoesNotTellTheAdministratorToEnableLdaps()
    {
        // Over an encrypted connection the refusal is not about encryption (channel binding enforcement is one
        // cause), so telling the administrator to switch on what is already on would send them in a circle.
        var refusal = new DirectoryOperationException(null, PlatformStrongAuthenticationText);

        var described = LdapBindRefusal.Describe(refusal, useSecureConnection: true);

        Assert.That(described, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(described!.Message, Does.Not.Contain("Enable"));
            Assert.That(described!.Message, Does.Contain("channel binding").IgnoreCase);
        }
    }

    [Test]
    public void Describe_InvalidCredentials_ReturnsNull()
    {
        var refusal = new LdapException(49, "The supplied credential is invalid.");

        Assert.That(LdapBindRefusal.Describe(refusal, useSecureConnection: false), Is.Null);
    }

    [Test]
    public void Describe_AnotherOperationErrorWithoutAResponse_ReturnsNull()
    {
        var refusal = new DirectoryOperationException(null, "The server is unwilling to process the request.");

        Assert.That(LdapBindRefusal.Describe(refusal, useSecureConnection: false), Is.Null);
    }

    [Test]
    public void Describe_AnotherOperationErrorWithAResponse_ReturnsNull()
    {
        var refusal = new DirectoryOperationException(CreateDirectoryResponse<ModifyResponse>(ResultCode.UnwillingToPerform), PlatformStrongAuthenticationText);

        Assert.That(LdapBindRefusal.Describe(refusal, useSecureConnection: false), Is.Null,
            "a response is authoritative: its result code decides, whatever the message says");
    }

    private static T CreateDirectoryResponse<T>(ResultCode resultCode) where T : DirectoryResponse =>
        (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: ["", Array.Empty<DirectoryControl>(), resultCode, "", Array.Empty<Uri>()],
            culture: null)!;
}
