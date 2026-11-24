// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Servers;
using JIM.Models.Localisation;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What an administrator's retry over the Exchange queue ran over is the Activity's target name, and it names the states
/// and failure kinds that were chosen. It is a message with a sentence per shape, so French and German read it as words
/// rather than as enum names, and the English is what it always was (#3094).
/// </summary>
[TestFixture]
public class ExchangeFilterMessageTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    [Test]
    public void DescribeFilter_EveryShape_RendersTheEnglishItWroteBefore()
    {
        var states = new ExchangeChangeState?[] { null }.Concat(Enum.GetValues<ExchangeChangeState>().Select(s => (ExchangeChangeState?)s));
        var kinds = new ExchangeChangeFailureKind?[] { null }.Concat(Enum.GetValues<ExchangeChangeFailureKind>().Select(k => (ExchangeChangeFailureKind?)k));
        var checkedShapes = 0;

        using (Assert.EnterMultipleScope())
        {
            foreach (var state in states)
            foreach (var kind in kinds)
            foreach (var search in new string?[] { null, "", "  ", " ada " })
            foreach (var withObject in new[] { false, true })
            {
                var filter = new PendingExchangeChangeFilter
                {
                    State = state,
                    FailureKind = kind,
                    SearchText = search,
                    MetaverseObjectId = withObject ? Guid.NewGuid() : null
                };
                var message = ExchangeDeliveryServer.DescribeFilter(filter);
                Assert.That(message.ToEnglish(), Is.EqualTo(Legacy(filter)), $"{state}, {kind}, search '{search}', object {withObject}");
                checkedShapes++;
            }

            foreach (var count in new[] { 1, 2, 30 })
            {
                var filter = new PendingExchangeChangeFilter { Ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList(), SearchText = "ignored" };
                Assert.That(ExchangeDeliveryServer.DescribeFilter(filter).ToEnglish(), Is.EqualTo(Legacy(filter)), $"{count} ids");
            }
        }

        Assert.That(checkedShapes, Is.GreaterThan(500));
    }

    [Test]
    public void DescribeFilter_InFrench_ReadsAsWholeSentences()
    {
        string In(PendingExchangeChangeFilter filter) => MessageRenderer.Render(ExchangeDeliveryServer.DescribeFilter(filter), French, French);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(In(new PendingExchangeChangeFilter()), Is.EqualTo("La file d’attente Exchange"));
            Assert.That(In(new PendingExchangeChangeFilter { Ids = [Guid.NewGuid()] }), Is.EqualTo("Une modification Exchange"));
            Assert.That(In(new PendingExchangeChangeFilter { Ids = [Guid.NewGuid(), Guid.NewGuid()] }), Is.EqualTo("2 modifications Exchange"));
            Assert.That(In(new PendingExchangeChangeFilter { State = ExchangeChangeState.Parked, FailureKind = ExchangeChangeFailureKind.Unreachable }),
                Is.EqualTo("Modifications Exchange : suspendues, inaccessible"));
            Assert.That(In(new PendingExchangeChangeFilter { State = ExchangeChangeState.Retrying, SearchText = " ada " }),
                Is.EqualTo("Modifications Exchange : nouvelle tentative, correspondant à « ada »"));
            Assert.That(In(new PendingExchangeChangeFilter { SearchText = "ada" }), Is.EqualTo("Modifications Exchange correspondant à « ada »"));
        }
    }

    [Test]
    public void DescribeFilter_InGerman_ReadsAsWholeSentences()
    {
        string In(PendingExchangeChangeFilter filter) => MessageRenderer.Render(ExchangeDeliveryServer.DescribeFilter(filter), German, German);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(In(new PendingExchangeChangeFilter()), Is.EqualTo("Die Exchange-Warteschlange"));
            Assert.That(In(new PendingExchangeChangeFilter { Ids = [Guid.NewGuid()] }), Is.EqualTo("Eine Exchange-Änderung"));
            Assert.That(In(new PendingExchangeChangeFilter { Ids = [Guid.NewGuid(), Guid.NewGuid()] }), Is.EqualTo("2 Exchange-Änderungen"));
            Assert.That(In(new PendingExchangeChangeFilter { State = ExchangeChangeState.Parked, FailureKind = ExchangeChangeFailureKind.AuthenticationFailed }),
                Is.EqualTo("Exchange-Änderungen: zurückgestellt, Authentifizierung fehlgeschlagen"));
            Assert.That(In(new PendingExchangeChangeFilter { FailureKind = ExchangeChangeFailureKind.Refused, SearchText = "ada" }),
                Is.EqualTo("Exchange-Änderungen: abgelehnt, die „ada“ entsprechen"));
            Assert.That(In(new PendingExchangeChangeFilter { SearchText = "ada" }), Is.EqualTo("Exchange-Änderungen, die „ada“ entsprechen"));
        }
    }

    [Test]
    public void DescribeFilter_EveryStateAndKind_HasAWordInEachLanguage()
    {
        foreach (var state in Enum.GetValues<ExchangeChangeState>())
        {
            var message = ExchangeDeliveryServer.DescribeFilter(new PendingExchangeChangeFilter { State = state });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(MessageRenderer.Render(message, French, French), Does.Not.Contain(state.ToString()), $"French for {state}");
                Assert.That(MessageRenderer.Render(message, German, German), Does.Not.Contain(state.ToString()), $"German for {state}");
            }
        }

        foreach (var kind in Enum.GetValues<ExchangeChangeFailureKind>())
        {
            var message = ExchangeDeliveryServer.DescribeFilter(new PendingExchangeChangeFilter { FailureKind = kind });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(MessageRenderer.Render(message, French, French), Does.Not.Contain(kind.ToString()), $"French for {kind}");
                Assert.That(MessageRenderer.Render(message, German, German), Does.Not.Contain(kind.ToString()), $"German for {kind}");
            }
        }
    }

    // The composer as it stood before the target name became a message.
    private static string Legacy(PendingExchangeChangeFilter filter)
    {
        if (filter.TargetsSpecificChanges)
            return filter.Ids!.Count == 1 ? "One Exchange change" : $"{filter.Ids!.Count} Exchange changes";

        var parts = new List<string>();
        if (filter.State.HasValue)
            parts.Add(filter.State.Value.ToString());
        if (filter.FailureKind.HasValue)
            parts.Add(filter.FailureKind.Value.ToString());

        var described = parts.Count == 0 && filter.MetaverseObjectId == null && filter.ConnectedSystemObjectId == null
                        && string.IsNullOrWhiteSpace(filter.SearchText)
            ? "The Exchange queue"
            : parts.Count == 0 ? "Exchange changes" : $"{string.Join(", ", parts)} Exchange changes";

        return string.IsNullOrWhiteSpace(filter.SearchText)
            ? described
            : $"{described} matching \"{filter.SearchText.Trim()}\"";
    }
}
