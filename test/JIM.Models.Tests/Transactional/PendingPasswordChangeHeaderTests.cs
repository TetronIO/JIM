// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The queue row as the surfaces list it (#1119, #1635): what "due" and "held" mean once a row knows where it
/// came from. A propagated change waits on its Connected System being switched on; an explicit set does not,
/// because the administrator named the account (decision D1), so the same switched-off system holds one and
/// not the other.
/// </summary>
[TestFixture]
public class PendingPasswordChangeHeaderTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);

    private static PendingPasswordChangeHeader Header(
        PendingPasswordChangeOrigin origin,
        bool takingPasswords,
        PendingPasswordChangeStatus status = PendingPasswordChangeStatus.Pending) => new()
    {
        Id = Guid.NewGuid(),
        Origin = origin,
        Status = status,
        ConnectedSystemTakingPasswords = takingPasswords
    };

    [Test]
    public void NewHeader_IsPropagated()
    {
        Assert.That(new PendingPasswordChangeHeader().Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
    }

    [Test]
    public void IsHeld_PropagatedChangeOnASwitchedOffSystem_IsTrue()
    {
        Assert.That(Header(PendingPasswordChangeOrigin.Propagated, takingPasswords: false).IsHeld, Is.True);
    }

    [Test]
    public void IsHeld_ExplicitChangeOnASwitchedOffSystem_IsFalse()
    {
        // The administrator named the account; the system being paused for propagation does not hold it.
        Assert.That(Header(PendingPasswordChangeOrigin.Explicit, takingPasswords: false).IsHeld, Is.False);
    }

    [Test]
    public void IsDue_ExplicitChangeOnASwitchedOffSystem_IsTrue()
    {
        Assert.That(Header(PendingPasswordChangeOrigin.Explicit, takingPasswords: false).IsDue(Now), Is.True);
    }

    [Test]
    public void IsDue_PropagatedChangeOnASwitchedOffSystem_IsFalse()
    {
        Assert.That(Header(PendingPasswordChangeOrigin.Propagated, takingPasswords: false).IsDue(Now), Is.False);
    }

    [Test]
    public void IsDue_ExplicitChangeWaitingOutABackoff_IsFalse()
    {
        var header = Header(PendingPasswordChangeOrigin.Explicit, takingPasswords: false);
        header.NextRetryAt = Now.AddMinutes(5);

        Assert.That(header.IsDue(Now), Is.False, "Origin decides whether a paused system holds the change, not whether a backoff does.");
    }

    [Test]
    public void IsDue_ExplicitChangeThatIsParked_IsFalse()
    {
        Assert.That(Header(PendingPasswordChangeOrigin.Explicit, takingPasswords: false, PendingPasswordChangeStatus.Parked).IsDue(Now), Is.False);
    }
}
