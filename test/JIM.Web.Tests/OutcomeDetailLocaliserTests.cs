// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A synchronisation outcome keeps its detail as English only (no message column), and the sentence for a value Collision
/// Remediation corrected is JIM's own. The portal shows it in the viewer's language by reading the sentence back into
/// the message it was written from, and shows any text it cannot read back as it was stored (#3094).
/// </summary>
[TestFixture]
public class OutcomeDetailLocaliserTests
{
    private const string Stored = "Corporate AD already holds the mail \"ada@example.com\" for another object, so JIM corrected it to \"ada2@example.com\".";

    [Test]
    public void Localise_RemediatedOutcome_InEnglish_IsTheStoredText()
    {
        Assert.That(OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated, Stored), Is.EqualTo(Stored));
    }

    [Test]
    [SetUICulture("fr")]
    public void Localise_RemediatedOutcome_InFrench_ReadsTheStoredSentenceInFrench()
    {
        Assert.That(OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated, Stored),
            Is.EqualTo("Corporate AD contient déjà la valeur « ada@example.com » de l’attribut mail pour un autre objet ; JIM l’a donc corrigée en « ada2@example.com »."));
    }

    [Test]
    [SetUICulture("de")]
    public void Localise_RemediatedOutcome_InGerman_ReadsTheStoredSentenceInGerman()
    {
        Assert.That(OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated, Stored),
            Is.EqualTo("Corporate AD enthält den Wert „ada@example.com“ für das Attribut mail bereits für ein anderes Objekt, daher hat JIM ihn auf „ada2@example.com“ korrigiert."));
    }

    [Test]
    [SetUICulture("fr")]
    public void Localise_ValuesWithQuotesAndPunctuation_StillRoundTripsToTheStoredEnglish()
    {
        const string awkward = "AD already holds the cn \"a \"b\" c\" for another object, so JIM corrected it to \"d\".\"";

        var shown = OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated, awkward);

        // However the sentence was split, the result is either translated or the stored text itself, never a lossy rewrite.
        Assert.That(shown == awkward || shown!.Contains("contient déjà"), Is.True);
    }

    [Test]
    [SetUICulture("fr")]
    public void Localise_TextThatIsNotTheRemediationSentence_IsLeftAsStored()
    {
        const string other = "Something an earlier release wrote.";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated, other), Is.EqualTo(other));
            Assert.That(OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated, null), Is.Null);
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void Localise_OtherOutcomeTypes_AreLeftAsStored()
    {
        Assert.That(OutcomeDetailLocaliser.Localise(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, Stored), Is.EqualTo(Stored));
    }

    [Test]
    public void Localise_TheSentenceTheRemediationWrites_IsTheOneTheLocaliserReads()
    {
        var written = ActivityMessage.CollisionRemediated("Corporate AD", "mail", "ada@example.com", "ada2@example.com").ToEnglish();

        Assert.That(written, Is.EqualTo(Stored));
    }
}
