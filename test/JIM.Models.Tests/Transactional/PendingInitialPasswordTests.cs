// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Reflection;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// Guards the record of an account awaiting its initial password.
/// <para>
/// The invariant worth a test is what this type must never gain. It is persisted, it is read by the portal, and
/// it is the one place in the initial password feature where somebody adding "just store the generated value so
/// we can show it again" would find a natural-looking home for it. A password JIM has set is unrecoverable by
/// design; making it recoverable here would quietly undo that.
/// </para>
/// </summary>
[TestFixture]
public class PendingInitialPasswordTests
{
    [Test]
    public void PendingInitialPassword_HasNowhereToStoreACredential()
    {
        // Names a future property might carry. Deliberately broad: this fails on a property called Password,
        // Secret, Credential or Token, which is the point. If one is ever genuinely needed, the argument for it
        // belongs in a review, not in a quiet rename to get past this.
        string[] credentialWords = ["password", "secret", "credential", "token", "passphrase"];

        var offenders = typeof(PendingInitialPassword)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            // The type's own name contains "Password", so a property is only suspect for words beyond that,
            // and for "password" appearing somewhere other than as part of the status or configuration link.
            .Where(p => credentialWords.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                        && p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"These properties could hold a credential: {string.Join(", ", offenders)}. A password JIM has set is not recoverable by design.");
    }

    [Test]
    public void PendingInitialPassword_StartsPendingAndUnattempted()
    {
        var pending = new PendingInitialPassword();

        Assert.Multiple(() =>
        {
            Assert.That(pending.Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
            Assert.That(pending.AttemptCount, Is.Zero);
            Assert.That(pending.FailureReason, Is.Null);
            Assert.That(pending.TargetMessage, Is.Null);
            Assert.That(pending.LastAttemptedAt, Is.Null);
            Assert.That(pending.Id, Is.Not.EqualTo(Guid.Empty), "an identity is needed before the row is written, as for a Pending Export");
        });
    }
}
