// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Models;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What an ambiguous match says on an import (and in a preview), and what several rules finding different Metaverse
/// Objects say, are held as messages so the portal shows them in the reader's language (#3094). The English is exactly
/// what the matching server wrote before, wherever the pieces of a sentence came from.
/// </summary>
[TestFixture]
public class ObjectMatchingAmbiguityMessageTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static ObjectMatchingRule Rule(string target = "Employee ID", string source = "employeeId") => new()
    {
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = target },
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = source } }]
    };

    private static MultipleMatchesException Ambiguity(ObjectMatchingRule? rule, int valueCount, int total)
    {
        var matches = Enumerable.Range(0, total).Select(_ => Guid.NewGuid()).ToList();
        return new MultipleMatchesException("ambiguous", matches)
        {
            Rule = rule,
            MatchValues = Enumerable.Range(1, valueCount).Select(i => (object)$"E{1000 + i}").ToList(),
            Candidates = matches.Take(2).Select(id => new ActivityRunProfileExecutionItemMatchCandidate { CandidateId = id, CandidateTotal = total }).ToList()
        };
    }

    [Test]
    public void DescribeAmbiguity_EveryShape_RendersTheEnglishItWroteBefore()
    {
        var checkedShapes = 0;
        using (Assert.EnterMultipleScope())
        {
            foreach (var rule in new[] { null, Rule() })
            foreach (var valueCount in new[] { 0, 1, 2, 10, 11, 25 })
            foreach (var total in new[] { 2, 3, 40 })
            foreach (var previewing in new[] { false, true })
            {
                var ambiguity = Ambiguity(rule, valueCount, total);
                Assert.That(ObjectMatchingServer.DescribeAmbiguity(ambiguity, previewing), Is.EqualTo(LegacyAmbiguity(ambiguity, previewing)));
                Assert.That(ObjectMatchingServer.DescribeAmbiguityMessage(ambiguity, previewing).ToEnglish(), Is.EqualTo(LegacyAmbiguity(ambiguity, previewing)));
                checkedShapes++;
            }
        }

        Assert.That(checkedShapes, Is.EqualTo(2 * 6 * 3 * 2));
    }

    [Test]
    public void DescribeAmbiguityMessage_InFrenchAndGerman_ReadsAsOneWholeSentenceNamingTheRule()
    {
        var ambiguity = Ambiguity(Rule(), valueCount: 12, total: 3);

        var french = MessageRenderer.Render(ObjectMatchingServer.DescribeAmbiguityMessage(ambiguity), French, French);
        var german = MessageRenderer.Render(ObjectMatchingServer.DescribeAmbiguityMessage(ambiguity, previewing: true), German, German);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(french, Does.StartWith("Cet objet correspond à 3 objets Metaverse selon Employee ID est égal à employeeId (\"E1001\", "));
            Assert.That(french, Does.Contain("et 2 de plus"));
            Assert.That(french, Does.Contain("il n’a été ni joint ni projeté"));
            Assert.That(french, Does.EndWith("afin que ses valeurs en identifient un seul."));
            Assert.That(german, Does.StartWith("Dieses Objekt entspricht 3 Metaverse-Objekten bei Employee ID entspricht employeeId (\"E1001\", "));
            Assert.That(german, Does.Contain("und 2 weitere"));
            Assert.That(german, Does.Contain("würde eine Synchronisierung es weder verknüpfen noch projizieren"));
            Assert.That(german, Does.EndWith("sodass seine Werte genau eines identifizieren."));
        }
    }

    [Test]
    public void DescribeAmbiguityMessage_NoRuleKnown_InGerman_OmitsTheComparison()
    {
        var text = MessageRenderer.Render(ObjectMatchingServer.DescribeAmbiguityMessage(Ambiguity(null, 0, 2)), German, German);

        Assert.That(text, Does.StartWith("Dieses Objekt entspricht 2 Metaverse-Objekten, daher gilt: "));
    }

    [Test]
    public void SyncJoinException_CarryingAMessage_RecordsTheMessageOnTheExecutionItem()
    {
        var message = ObjectMatchingServer.DescribeAmbiguityMessage(Ambiguity(Rule(), 2, 2));
        var exception = new SyncJoinException(ActivityRunProfileExecutionItemErrorType.AmbiguousMatch, message);
        var item = new ActivityRunProfileExecutionItem();

        JIM.Application.Localisation.ActivityMessage.SetError(item, exception);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo(message.ToEnglish()));
            Assert.That(item.ErrorMessage, Is.EqualTo(message.ToEnglish()));
            Assert.That(item.ErrorMessageDetailJson, Is.EqualTo(MessageJson.Serialise(message)));
        }
    }

    [Test]
    public void DescribeImportRuleDisagreement_EveryShape_RendersTheEnglishItWroteBefore()
    {
        var first = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Ada Lovelace" };
        var second = new MetaverseObject { Id = Guid.NewGuid() };
        var third = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Zed" };
        var oneRule = Rule();
        var otherRule = Rule("Mail", "email");

        foreach (var previewing in new[] { false, true })
        {
            var matches = new List<ImportRuleMatch>
            {
                new(new SyncRule { Name = "HR rule" }, new ImportMatch(first, oneRule, null!, "E1001")),
                new(new SyncRule { Name = "Mail rule" }, new ImportMatch(second, otherRule, null!, "ada@example.com")),
                new(new SyncRule { Name = "Third" }, new ImportMatch(third, oneRule, null!, "E1001"))
            };

            var disagreement = ObjectMatchingServer.DescribeImportRuleDisagreement(matches, previewing)!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(disagreement.Message, Is.EqualTo(LegacyDisagreement(matches, previewing)));
                Assert.That(disagreement.MessageDetail!.ToEnglish(), Is.EqualTo(disagreement.Message));
            }
        }
    }

    [Test]
    public void DescribeImportRuleDisagreement_InFrench_ListsEachRuleWithItsComparison()
    {
        var first = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Ada" };
        var second = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Grace" };
        var matches = new List<ImportRuleMatch>
        {
            new(new SyncRule { Name = "HR" }, new ImportMatch(first, Rule(), null!, "E1")),
            new(new SyncRule { Name = "Mail" }, new ImportMatch(second, Rule("Mail", "email"), null!, "a@b"))
        };

        var text = MessageRenderer.Render(ObjectMatchingServer.DescribeImportRuleDisagreement(matches)!.MessageDetail!, French, French);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.StartWith("Les règles de synchronisation d’importation de cet objet correspondent à 2 objets Metaverse différents : "));
            Assert.That(text, Does.Contain("« HR » correspond à Ada selon Employee ID est égal à employeeId (« E1 ») ; « Mail » correspond à Grace selon Mail est égal à email (« a@b »)"));
            Assert.That(text, Does.Contain("il n’a été ni joint ni projeté"));
        }
    }

    // The composers as they stood before these sentences became messages.
    private static string LegacyAmbiguity(MultipleMatchesException ambiguity, bool previewing)
    {
        var outcome = previewing ? "a synchronisation would neither join nor project it" : "it was neither joined nor projected";
        var count = ambiguity.Candidates.Count > 0 ? ambiguity.Candidates[0].CandidateTotal : ambiguity.Matches.Count;
        var matchedOn = string.Empty;
        if (ambiguity.Rule != null)
        {
            var values = ambiguity.MatchValues.Take(10).Select(value => $"\"{Convert.ToString(value, CultureInfo.InvariantCulture)}\"").ToList();
            if (ambiguity.MatchValues.Count > 10)
                values.Add($"and {ambiguity.MatchValues.Count - 10} more");
            matchedOn = $" on {ambiguity.Rule.DescribeComparison()} ({string.Join(", ", values)})";
        }

        return $"This object matches {count} Metaverse Objects{matchedOn}, so {outcome}. " +
               "A Connected System Object can join only one Metaverse Object: correct the data, or the Object Matching Rule, " +
               "so that its values identify one.";
    }

    private static string LegacyDisagreement(IReadOnlyList<ImportRuleMatch> matches, bool previewing)
    {
        var distinct = matches.DistinctBy(m => m.Match.MetaverseObject.Id).ToList();
        var found = string.Join("; ", matches.Select(m =>
            $"'{m.SyncRule.Name}' matches {m.Match.MetaverseObject.NameOrId} on {m.Match.Rule.DescribeComparison()} (\"{m.Match.MatchedValue}\")"));
        var outcome = previewing ? "a synchronisation would neither join nor project it" : "it was neither joined nor projected";
        return $"This object's import Synchronisation Rules match {distinct.Count} different Metaverse Objects: {found}. " +
               $"JIM does not choose between them, so {outcome}. Correct the data, or the rules' Object Matching Rules, " +
               "so that they identify one Metaverse Object.";
    }
}
