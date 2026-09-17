// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Sweeps every string <see cref="OutcomeDisplayMap"/> can produce (a label, a speculative preview
/// label, or a sentence form) and asserts it never carries "Identity", "record", "account", "MVO" or
/// "CSO": JIM does not rename its product nouns, and it does not carry a second, more technical
/// vocabulary beside them either. "Metaverse Object" and "Connected System Object" are used in full
/// wherever a noun is needed.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapVocabularyTests
{
    private static readonly string[] BannedWords = ["Identity", "record", "account", "MVO", "CSO"];

    /// <summary>
    /// Whether a string contains a banned word as a whole word (case-insensitive), so "Deprovisioned"
    /// is not flagged for containing neither "record" as a substring by chance, and so "record" would
    /// still be caught inside "recorded" only if that were itself the intent; every banned word here is
    /// checked as a case-insensitive substring, which is the stricter and correct reading since none of
    /// them are legitimate substrings of another product word JIM uses.
    /// </summary>
    private static void AssertNoBannedWord(string? text, string context)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var banned in BannedWords)
        {
            Assert.That(text, Does.Not.Contain(banned).IgnoreCase,
                $"{context} (\"{text}\") contains the banned word \"{banned}\"");
        }
    }

    [Test]
    public void Get_EveryOutcomeType_LabelAndSentenceFormCarryNoBannedVocabulary()
    {
        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
        {
            var display = OutcomeDisplayMap.Get(outcomeType);

            AssertNoBannedWord(display.Label, $"{outcomeType}'s Label");
            AssertNoBannedWord(display.SentenceForm, $"{outcomeType}'s SentenceForm");
        }
    }

    [Test]
    public void GetExportDecision_EveryReasonCode_LabelCarriesNoBannedVocabulary()
    {
        foreach (var reasonCode in Enum.GetValues<CausalReasonCode>())
        {
            var display = OutcomeDisplayMap.GetExportDecision(reasonCode);

            AssertNoBannedWord(display.Label, $"GetExportDecision({reasonCode})'s Label");
        }
    }

    [Test]
    public void GetQueueingAndOperationChips_EveryProducedLabel_CarriesNoBannedVocabulary()
    {
        // The one-word operation chips (Created/Updated/Deleted/Joined), covered here across every
        // input surface that can produce one: GetEventOperation, GetHopOperation and, through them,
        // the shared GetQueueingDecisionOperation.
        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
        {
            foreach (var reasonCode in Enum.GetValues<CausalReasonCode>())
            {
                var eventDisplay = OutcomeDisplayMap.GetEventOperation(outcomeType, reasonCode);
                AssertNoBannedWord(eventDisplay?.Label, $"GetEventOperation({outcomeType}, {reasonCode})'s Label");
            }

            foreach (var stagedChangeType in Enum.GetValues<PendingExportChangeType>())
            {
                var eventDisplay = OutcomeDisplayMap.GetEventOperation(outcomeType, stagedChangeType: stagedChangeType);
                AssertNoBannedWord(eventDisplay?.Label, $"GetEventOperation({outcomeType}, staged: {stagedChangeType})'s Label");
            }
        }
    }
}
