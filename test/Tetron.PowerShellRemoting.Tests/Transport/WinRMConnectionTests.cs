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
}
