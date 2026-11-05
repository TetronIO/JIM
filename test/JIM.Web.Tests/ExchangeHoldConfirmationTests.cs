// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// When saving a Connected System's settings asks first (#2021, decided 2026-11-05): only a save that turns Exchange
/// recipient management off, since that holds every change waiting for delivery.
/// </summary>
[TestFixture]
public class ExchangeHoldConfirmationTests
{
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    public void IsNeeded_OnlyWhenTheSaveTurnsManagementOff(bool managedAsStored, bool managedAfterSave, bool expected)
    {
        Assert.That(ExchangeHoldConfirmation.IsNeeded(managedAsStored, managedAfterSave), Is.EqualTo(expected));
    }
}
