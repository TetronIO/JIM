// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Application.Servers;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Password failure reasons and queue statuses are enums, and JIM used to print their names into the text an administrator
/// reads. Each value now has its own message, so the portal says "erreur de configuration" where it said
/// "ConfigurationFault", while the English stays the name it always was (#3094).
/// </summary>
[TestFixture]
public class PasswordEnumMessageTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    [Test]
    public void FailureReasonMessage_EveryValue_RendersTheEnumNameInEnglishAndAWordInEachLanguage()
    {
        foreach (var reason in Enum.GetValues<PasswordSetFailureReason>())
        {
            var message = ActivityMessage.PasswordFailureReason(reason);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(message.ToEnglish(), Is.EqualTo(reason.ToString()));
                Assert.That(MessageRenderer.Render(message, French, French), Is.Not.EqualTo(reason.ToString()), $"French for {reason}");
                Assert.That(MessageRenderer.Render(message, German, German), Is.Not.EqualTo(reason.ToString()), $"German for {reason}");
            }
        }
    }

    [Test]
    public void StatusMessage_EveryValue_RendersTheEnumNameInEnglishAndAWordInEachLanguage()
    {
        foreach (var status in Enum.GetValues<PendingPasswordChangeStatus>())
        {
            var message = ActivityMessage.PasswordChangeStatus(status);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(message.ToEnglish(), Is.EqualTo(status.ToString()));
                Assert.That(MessageRenderer.Render(message, French, French), Is.Not.EqualTo(status.ToString()), $"French for {status}");
                Assert.That(MessageRenderer.Render(message, German, German), Is.Not.EqualTo(status.ToString()), $"German for {status}");
            }
        }
    }

    [Test]
    public void DescribeFilter_EveryCombination_RendersTheEnglishItWroteBefore()
    {
        var statuses = new PendingPasswordChangeStatus?[] { null }.Concat(Enum.GetValues<PendingPasswordChangeStatus>().Select(s => (PendingPasswordChangeStatus?)s));
        var reasons = new PasswordSetFailureReason?[] { null }.Concat(Enum.GetValues<PasswordSetFailureReason>().Select(r => (PasswordSetFailureReason?)r));

        using (Assert.EnterMultipleScope())
        {
            foreach (var status in statuses)
            foreach (var reason in reasons)
            {
                var filter = new PendingPasswordChangeFilter { Status = status, FailureReason = reason };
                Assert.That(PasswordSynchronisationServer.DescribeFilter(filter).ToEnglish(), Is.EqualTo(LegacyFilter(filter)), $"{status}, {reason}");
            }

            foreach (var count in new[] { 1, 2, 17 })
            {
                var filter = new PendingPasswordChangeFilter { Ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList() };
                Assert.That(PasswordSynchronisationServer.DescribeFilter(filter).ToEnglish(), Is.EqualTo(LegacyFilter(filter)), $"{count} ids");
            }
        }
    }

    [Test]
    public void DescribeFilter_StatusAndReason_ReadAsWordsInFrenchAndGerman()
    {
        var filter = new PendingPasswordChangeFilter { Status = PendingPasswordChangeStatus.Parked, FailureReason = PasswordSetFailureReason.ConfigurationFault };

        var message = PasswordSynchronisationServer.DescribeFilter(filter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageRenderer.Render(message, French, French), Is.EqualTo("Modifications de mot de passe : suspendues, erreur de configuration"));
            Assert.That(MessageRenderer.Render(message, German, German), Is.EqualTo("Kennwortänderungen: zurückgestellt, Konfigurationsfehler"));
        }
    }

    [Test]
    public void DescribeFailure_NoWordsFromTheTarget_NamesTheReasonInTheReadersLanguage()
    {
        var system = new JIM.Models.Staging.ConnectedSystem { Name = "Corporate AD" };
        var change = new PendingPasswordChange { Status = PendingPasswordChangeStatus.Pending, FailureReason = PasswordSetFailureReason.PolicyRejection };

        var message = PasswordSynchronisationServer.DescribeFailure(system, change);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo("Password not set on Corporate AD: PolicyRejection. JIM will try again."));
            Assert.That(MessageRenderer.Render(message, French, French), Does.StartWith("Mot de passe non défini sur Corporate AD : rejet par la stratégie."));
            Assert.That(MessageRenderer.Render(message, German, German), Does.StartWith("Kennwort auf Corporate AD nicht gesetzt: Ablehnung durch Richtlinie."));
        }
    }

    [Test]
    public void DescribeFailure_TheTargetGaveWords_KeepsThemAsWritten()
    {
        var system = new JIM.Models.Staging.ConnectedSystem { Name = "Corporate AD" };
        var change = new PendingPasswordChange { FailureReason = PasswordSetFailureReason.PolicyRejection, TargetMessage = "Too short" };

        Assert.That(PasswordSynchronisationServer.DescribeFailure(system, change).ToEnglish(), Does.Contain("Corporate AD: Too short."));
    }

    private static string LegacyFilter(PendingPasswordChangeFilter filter)
    {
        if (filter.TargetsSpecificChanges)
            return filter.Ids!.Count == 1 ? "One password change" : $"{filter.Ids!.Count} password changes";

        var parts = new List<string>();
        if (filter.Status.HasValue)
            parts.Add(filter.Status.Value.ToString());
        if (filter.FailureReason.HasValue)
            parts.Add(filter.FailureReason.Value.ToString());

        return parts.Count == 0 ? "The Password Synchronisation queue" : $"{string.Join(", ", parts)} password changes";
    }
}
