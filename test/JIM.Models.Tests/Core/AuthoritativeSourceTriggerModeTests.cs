// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Pins the numeric values of <see cref="AuthoritativeSourceTriggerMode"/> and the deliberate split between
/// the enum's zero value and the <see cref="MetaverseObjectType"/> property initialiser (#119):
/// existing database rows read the added column's default value 0 (SpecificSourcesDisconnect), preserving
/// pre-existing behaviour with no backfill, while new entities constructed in code start at the safe
/// default (AllSourcesDisconnect). Renumbering either side silently flips deletion semantics for
/// customers, so both sides are pinned here.
/// </summary>
[TestFixture]
public class AuthoritativeSourceTriggerModeTests
{
    [Test]
    public void AuthoritativeSourceTriggerMode_SpecificSourcesDisconnect_HasValueZero()
    {
        Assert.That((int)AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect, Is.EqualTo(0));
    }

    [Test]
    public void AuthoritativeSourceTriggerMode_AllSourcesDisconnect_HasValueOne()
    {
        Assert.That((int)AuthoritativeSourceTriggerMode.AllSourcesDisconnect, Is.EqualTo(1));
    }

    [Test]
    public void MetaverseObjectType_NewInstance_DefaultsToAllSourcesDisconnect()
    {
        var objectType = new MetaverseObjectType();
        Assert.That(objectType.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
    }
}
