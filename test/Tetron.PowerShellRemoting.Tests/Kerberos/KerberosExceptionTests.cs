// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

public class KerberosExceptionTests
{
    private const uint CredentialsExpired = 0x000B0000;
    private const uint Failure = 0x000D0000;
    private const uint TicketExpired = 0x96C73A20;
    private const uint PreauthenticationFailed = 0x96C73A18;

    [Test]
    public void IsCredentialExpired_CredentialsExpiredMajorStatus_IsTrue()
    {
        Assert.That(new KerberosException("gss_init_sec_context", CredentialsExpired, 0, "expired").IsCredentialExpired, Is.True);
    }

    [Test]
    public void IsCredentialExpired_FailureWithTheTicketExpiredMinorStatus_IsTrue()
    {
        // The KDC refusing a TGS request for an expired ticket-granting ticket surfaces as a failure, not as expiry.
        Assert.That(new KerberosException("gss_init_sec_context", Failure, TicketExpired, "expired").IsCredentialExpired, Is.True);
    }

    [Test]
    public void IsCredentialExpired_AnyOtherFailure_IsFalse()
    {
        Assert.That(new KerberosException("gss_acquire_cred_with_password", Failure, PreauthenticationFailed, "wrong password").IsCredentialExpired, Is.False);
    }
}
