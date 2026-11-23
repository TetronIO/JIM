// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// JIM's built-in Schedules and Roles are translated by their stable name (#3030). A Schedule or Role an administrator
/// created is shown as stored, whatever the viewer's language.
/// </summary>
[TestFixture]
public class BuiltInDisplayTests
{
    [Test]
    public void BuiltInText_EveryBuiltInSchedule_HasANameAndADescription()
    {
        foreach (var key in new[] { "TemporalScopeReconciliation", "HistoryRetentionCleanup" })
        {
            Assert.That(BuiltInText.ResourceManager.GetString("Schedule_" + key, CultureInfo.InvariantCulture), Is.Not.Null, key);
            Assert.That(BuiltInText.ResourceManager.GetString("Schedule_" + key + "_Description", CultureInfo.InvariantCulture), Is.Not.Null, key);
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void ScheduleName_BuiltInSchedule_ReturnsTheFrenchName()
    {
        Assert.That(BuiltInDisplay.ScheduleName("Temporal Scope Reconciliation", builtIn: true), Is.EqualTo("Réconciliation du périmètre temporel"));
    }

    [Test]
    [SetUICulture("fr")]
    public void ScheduleName_CustomerSchedule_ReturnsTheStoredName()
    {
        Assert.That(BuiltInDisplay.ScheduleName("Temporal Scope Reconciliation", builtIn: false), Is.EqualTo("Temporal Scope Reconciliation"));
    }

    [Test]
    [SetUICulture("fr")]
    public void RoleName_BuiltInAdministratorRole_ReturnsTheFrenchName()
    {
        Assert.That(BuiltInDisplay.RoleName("Administrator", builtIn: true), Is.EqualTo("Administrateur"));
    }

    [Test]
    [SetUICulture("fr")]
    public void RoleName_CustomerRole_ReturnsTheStoredName()
    {
        Assert.That(BuiltInDisplay.RoleName("Auditors", builtIn: false), Is.EqualTo("Auditors"));
    }
}
