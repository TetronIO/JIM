// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Services;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Trusting a certificate from the identity provider banner refreshes the Trusted Certificates page open beneath it
/// (#2363), and a page that has gone away hears nothing.
/// </summary>
[TestFixture]
public class TrustedCertificateChangesTests
{
    [Test]
    public async Task NotifyAsync_EverySubscriberHearsItAsync()
    {
        var changes = new TrustedCertificateChanges();
        var heard = 0;
        changes.Changed += () => { heard++; return Task.CompletedTask; };
        changes.Changed += () => { heard++; return Task.CompletedTask; };

        await changes.NotifyAsync();

        Assert.That(heard, Is.EqualTo(2));
    }

    [Test]
    public async Task NotifyAsync_UnsubscribedHandler_NotCalledAsync()
    {
        var changes = new TrustedCertificateChanges();
        var heard = 0;
        Task Handler() { heard++; return Task.CompletedTask; }
        changes.Changed += Handler;
        changes.Changed -= Handler;

        await changes.NotifyAsync();

        Assert.That(heard, Is.Zero);
    }

    [Test]
    public void NotifyAsync_NobodyListening_DoesNothing()
    {
        Assert.That(async () => await new TrustedCertificateChanges().NotifyAsync(), Throws.Nothing);
    }
}
