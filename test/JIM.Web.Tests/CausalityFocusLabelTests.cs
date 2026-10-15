// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How the Causality Explorer's trail names each focus (#2100): an item by the object it was about and the run that
/// recorded it, an object by its type and name, and anything no longer found by saying so.
/// </summary>
[TestFixture]
public class CausalityFocusLabelTests
{
    [Test]
    public void For_Item_NamesTheObjectAndTheRun()
    {
        var focus = new CausalityFocus(CausalityFocusKind.Item, Guid.NewGuid());
        var labels = new CausalFocusLabels
        {
            Items = { [focus.Id] = new CausalTimelineEntry { ObjectType = "group", DisplayName = "Project-Pulse", ActivityTargetName = "Full Synchronisation" } }
        };

        Assert.That(CausalityFocusLabel.For(focus, labels), Is.EqualTo("group: Project-Pulse · Full Synchronisation"));
    }

    [Test]
    public void For_ItemWithNoNameOrType_FallsBackToItsExternalIdAlone()
    {
        var focus = new CausalityFocus(CausalityFocusKind.Item, Guid.NewGuid());
        var labels = new CausalFocusLabels
        {
            Items = { [focus.Id] = new CausalTimelineEntry { ExternalId = "S8-99", ActivityTargetName = "Delta Import" } }
        };

        Assert.That(CausalityFocusLabel.For(focus, labels), Is.EqualTo("S8-99 · Delta Import"));
    }

    [Test]
    public void For_Object_NamesItsTypeAndName()
    {
        var focus = new CausalityFocus(CausalityFocusKind.ConnectedSystemObject, Guid.NewGuid());
        var labels = new CausalFocusLabels
        {
            Objects = { [focus.Subject!] = new CausalObjectHeader { Subject = focus.Subject!, ObjectTypeName = "person", DisplayName = "tina.adams99" } }
        };

        Assert.That(CausalityFocusLabel.For(focus, labels), Is.EqualTo("person: tina.adams99"));
    }

    [Test]
    public void For_ObjectWithNoName_IsNamedByItsExternalId()
    {
        var focus = new CausalityFocus(CausalityFocusKind.ConnectedSystemObject, Guid.NewGuid());
        var labels = new CausalFocusLabels
        {
            Objects = { [focus.Subject!] = new CausalObjectHeader { Subject = focus.Subject!, ObjectTypeName = "person", ExternalId = "S8-99" } }
        };

        Assert.That(CausalityFocusLabel.For(focus, labels), Is.EqualTo("person: S8-99"));
    }

    [TestCase(CausalityFocusKind.Item)]
    [TestCase(CausalityFocusKind.ConnectedSystemObject)]
    [TestCase(CausalityFocusKind.MetaverseObject)]
    public void For_FocusNoLongerFound_SaysSo(CausalityFocusKind kind)
    {
        var focus = new CausalityFocus(kind, Guid.NewGuid());

        Assert.That(CausalityFocusLabel.For(focus, new CausalFocusLabels()), Is.EqualTo("No longer retained"));
    }

    [Test]
    public void For_LabelsNotYetRead_SaysNothingMisleading()
    {
        var focus = new CausalityFocus(CausalityFocusKind.Item, Guid.NewGuid());

        Assert.That(CausalityFocusLabel.For(focus, labels: null), Is.EqualTo("Execution item"));
    }
}
