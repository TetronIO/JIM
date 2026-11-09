// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using Tetron.PowerShellRemoting.Transport;

namespace Tetron.PowerShellRemoting.Tests.Transport;

public class WinRMConnectionTests
{
    [Test]
    public void NewPost_AnyRequest_AsksForTheReplyUncompressed()
    {
        // Exchange's endpoint is hosted by IIS, which compresses replies when a client allows it. A compressed reply
        // cannot be read as an encrypted WinRM message, so every request says so explicitly.
        using var request = WinRMConnection.NewPost(new Uri("http://ex1.panoply.local/PowerShell/"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Version, Is.EqualTo(HttpVersion.Version11));
            Assert.That(request.Headers.AcceptEncoding.Select(e => e.Value), Is.EqualTo(new[] { "identity" }));
        }
    }

    [Test]
    public void HandshakeFailure_Http401_IsKerberosRefused()
    {
        // The server answered the handshake and said no: the account, its password or its rights.
        var failure = WinRMConnection.HandshakeFailure(HttpStatusCode.Unauthorized, "svc-jim@PANOPLY.LOCAL");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Subcode, Is.EqualTo("AccessDenied"));
            Assert.That(failure.Message, Does.Contain("refused Kerberos authentication for svc-jim@PANOPLY.LOCAL"));
            Assert.That(RemoteFailure.FromException(failure).Kind, Is.EqualTo(RemoteFailureKind.Authentication));
        }
    }

    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.BadGateway)]
    public void HandshakeFailure_ServerError_IsAnHttpErrorNotARefusal(HttpStatusCode status)
    {
        // IIS answers 503 while Exchange's web services are stopped or still starting. That is the server being
        // unavailable, which a retry may cure; reporting it as refused authentication sends an administrator after
        // the account during an outage.
        var failure = WinRMConnection.HandshakeFailure(status, "svc-jim@PANOPLY.LOCAL");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Subcode, Is.EqualTo("HttpError"));
            Assert.That(failure.Message, Does.Contain($"HTTP {(int)status}"));
            Assert.That(failure.Message, Does.Not.Contain("refused"));
            Assert.That(RemoteFailure.FromException(failure).Kind, Is.Not.EqualTo(RemoteFailureKind.Authentication));
        }
    }
}
