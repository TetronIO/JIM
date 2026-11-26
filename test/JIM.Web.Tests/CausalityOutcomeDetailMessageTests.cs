// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Application.Localisation;
using JIM.Models.Activities;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A synchronisation outcome whose detail JIM worded itself (the sentence for a value Collision Remediation corrected)
/// stores it as a message beside the English (#3094). The causality view shows the message in the viewer's language,
/// and shows the English as it was stored where the outcome has no message (one written before messages existed).
/// </summary>
[TestFixture]
public class CausalityOutcomeDetailMessageTests
{
    private const string English = "Corporate AD already holds the mail \"ada@example.com\" for another object, so JIM corrected it to \"ada2@example.com\".";

    private static CausalityEvent BuildRemediated(Action<ActivityRunProfileExecutionItemSyncOutcome> configure)
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var outcome = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated,
            parent: null, ordinal: 0, detailCount: 1);
        configure(outcome);

        return CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext()).Roots[0];
    }

    private static void StoreMessage(ActivityRunProfileExecutionItemSyncOutcome outcome) =>
        outcome.DetailMessageDetail = ActivityMessage.CollisionRemediated("Corporate AD", "mail", "ada@example.com", "ada2@example.com");

    [Test]
    public void Build_RemediatedOutcomeWithAStoredMessage_InEnglish_ShowsTheEnglish()
    {
        Assert.That(BuildRemediated(StoreMessage).DetailMessage, Is.EqualTo(English));
    }

    [Test]
    [SetUICulture("fr")]
    public void Build_RemediatedOutcomeWithAStoredMessage_InFrench_ShowsItInFrench()
    {
        Assert.That(BuildRemediated(StoreMessage).DetailMessage,
            Is.EqualTo("Corporate AD contient déjà la valeur « ada@example.com » de l’attribut mail pour un autre objet ; JIM l’a donc corrigée en « ada2@example.com »."));
    }

    [Test]
    [SetUICulture("fr")]
    public void Build_RemediatedOutcomeWithOnlyItsEnglish_ShowsTheEnglishAsStored()
    {
        Assert.That(BuildRemediated(outcome => outcome.DetailMessage = English).DetailMessage, Is.EqualTo(English));
    }

    [Test]
    public void StoredMessage_RendersTheEnglishTheRestApiReturns()
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome();
        StoreMessage(outcome);

        Assert.That(outcome.DetailMessage, Is.EqualTo(English));
    }
}
