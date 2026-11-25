// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Globalization;
using System.Linq;
using JIM.Models.Localisation;
using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

/// <summary>
/// The factory reset summary is held as a message, so the portal shows it in the reader's language with each count
/// pluralised by that language's own rule (#3094). The English is exactly what the summary was before.
/// </summary>
[TestFixture]
public class SystemResetMessageTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static readonly int[] Counts = [0, 1, 2, 1119, 25000];

    private static readonly Action<SystemResetResult, int>[] Setters =
    [
        (r, n) => r.ConnectedSystemsRemoved = n,
        (r, n) => r.ConnectedSystemObjectsRemoved = n,
        (r, n) => r.MetaverseObjectsRemoved = n,
        (r, n) => r.MetaverseObjectChangesRemoved = n,
        (r, n) => r.ConnectedSystemObjectChangesRemoved = n,
        (r, n) => r.SyncRulesRemoved = n,
        (r, n) => r.ObjectMatchingRulesRemoved = n,
        (r, n) => r.SchedulesRemoved = n,
        (r, n) => r.ScheduleExecutionsRemoved = n,
        (r, n) => r.ActivitiesRemoved = n,
        (r, n) => r.PendingExportsRemoved = n,
        (r, n) => r.CustomMetaverseObjectTypesRemoved = n,
        (r, n) => r.CustomMetaverseAttributesRemoved = n,
        (r, n) => r.CustomRolesRemoved = n,
        (r, n) => r.CustomConnectorDefinitionsRemoved = n,
        (r, n) => r.CustomPredefinedSearchesRemoved = n,
        (r, n) => r.CustomExampleDataSetsRemoved = n,
        (r, n) => r.CustomExampleDataTemplatesRemoved = n,
        (r, n) => r.CustomApiKeysRemoved = n,
        (r, n) => r.TrustedCertificatesRemoved = n
    ];

    [Test]
    public void BuildResetMessageDetail_EveryCategoryAndCount_RendersTheEnglishTheSummaryWroteBefore()
    {
        var checkedSummaries = 0;
        using (Assert.EnterMultipleScope())
        {
            foreach (var includeAdministrators in new[] { true, false })
            foreach (var retained in new[] { 0, 1, 3 })
            foreach (var setter in Setters)
            foreach (var count in Counts)
            {
                var result = new SystemResetResult { AdministratorsRetained = retained };
                setter(result, count);

                var detail = result.BuildResetMessageDetail(includeAdministrators);
                var expected = Legacy.Build(result, includeAdministrators);
                Assert.That(detail.ToEnglish(), Is.EqualTo(expected), $"count {count}, administrators included {includeAdministrators}, retained {retained}");
                Assert.That(result.BuildResetMessage(includeAdministrators), Is.EqualTo(expected));
                checkedSummaries++;
            }
        }

        Assert.That(checkedSummaries, Is.EqualTo(2 * 3 * 20 * 5));
    }

    /// <summary>The administrators retained travel as a number, so the count is formatted for the reader.</summary>
    [Test]
    public void BuildResetMessageDetail_AdministratorsRetained_IsANumber()
    {
        var detail = new SystemResetResult { AdministratorsRetained = 3 }.BuildResetMessageDetail(includeAdministrators: false);
        var header = detail.Arguments[0].Message!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Arguments[0].Number, Is.EqualTo(3));
            Assert.That(header.Arguments[0].Text, Is.Null);
        }
    }

    [Test]
    public void BuildResetMessageDetail_EveryCategoryAtOnce_RendersTheEnglishTheSummaryWroteBefore()
    {
        foreach (var count in Counts)
        {
            var result = new SystemResetResult { AdministratorsRetained = 2 };
            foreach (var setter in Setters)
                setter(result, count);

            Assert.That(result.BuildResetMessageDetail(false).ToEnglish(), Is.EqualTo(Legacy.Build(result, false)), $"count {count}");
        }
    }

    [Test]
    public void BuildResetMessageDetail_InFrench_PluralisesEachCountAndReadsAsWholeSentences()
    {
        var result = new SystemResetResult
        {
            ConnectedSystemsRemoved = 2,
            SyncRulesRemoved = 1,
            ActivitiesRemoved = 1,
            MetaverseObjectsRemoved = 1119,
            AdministratorsRetained = 1
        };

        var text = MessageRenderer.Render(result.BuildResetMessageDetail(includeAdministrators: false), French, French);

        Assert.That(text, Does.StartWith("Réinitialisation d’usine terminée (administrateurs conservés : 1)."));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("Supprimé :"));
            Assert.That(text, Does.Contain("• 2 systèmes connectés"));
            Assert.That(text, Does.Contain("• 1 règle de synchronisation"));
            Assert.That(text, Does.Not.Contain("règles de synchronisation"));
            Assert.That(text, Does.Contain("• 1 activité"));
            Assert.That(text, Does.Contain("objets Metaverse"));
        }
    }

    [Test]
    public void BuildResetMessageDetail_InGerman_PluralisesEachCountAndReadsAsWholeSentences()
    {
        var result = new SystemResetResult
        {
            ConnectedSystemsRemoved = 2,
            ConnectedSystemObjectsRemoved = 1,
            TrustedCertificatesRemoved = 1,
            AdministratorsRetained = 2
        };

        var text = MessageRenderer.Render(result.BuildResetMessageDetail(includeAdministrators: false), German, German);

        Assert.That(text, Does.StartWith("Zurücksetzen auf Werkseinstellungen abgeschlossen (Administratoren beibehalten: 2)."));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("• 2 verbundene Systeme"));
            Assert.That(text, Does.Contain("• 1 Objekt im verbundenen System"));
            Assert.That(text, Does.Contain("• 1 vertrauenswürdiges Zertifikat"));
        }
    }

    [Test]
    public void BuildResetMessageDetail_NothingRemoved_InGerman_SaysTheSystemWasAlreadyEmpty()
    {
        var text = MessageRenderer.Render(new SystemResetResult().BuildResetMessageDetail(includeAdministrators: true), German, German);

        Assert.That(text, Does.StartWith("Zurücksetzen auf Werkseinstellungen abgeschlossen (Administratoren entfernt)."));
        Assert.That(text, Does.Contain("Es wurde nichts entfernt; das System war bereits leer."));
    }

    // The composer as it stood before the summary became a message, kept here to assert the English never moves.
    private static class Legacy
    {
        public static string Build(SystemResetResult r, bool includeAdministrators)
        {
            var header = includeAdministrators
                ? "Factory reset completed (administrators removed)."
                : $"Factory reset completed (administrators retained: {r.AdministratorsRetained}).";

            var categories = new (int Count, string Singular, string Plural)[]
            {
                (r.ConnectedSystemsRemoved, "Connected System", "Connected Systems"),
                (r.ConnectedSystemObjectsRemoved, "Connected System Object", "Connected System Objects"),
                (r.MetaverseObjectsRemoved, "Metaverse Object", "Metaverse Objects"),
                (r.MetaverseObjectChangesRemoved, "Metaverse Object change record", "Metaverse Object change records"),
                (r.ConnectedSystemObjectChangesRemoved, "Connected System Object change record", "Connected System Object change records"),
                (r.SyncRulesRemoved, "Synchronisation Rule", "Synchronisation Rules"),
                (r.ObjectMatchingRulesRemoved, "Object Matching Rule", "Object Matching Rules"),
                (r.SchedulesRemoved, "schedule", "schedules"),
                (r.ScheduleExecutionsRemoved, "schedule execution", "schedule executions"),
                (r.ActivitiesRemoved, "activity", "activities"),
                (r.PendingExportsRemoved, "Pending Export", "Pending Exports"),
                (r.CustomMetaverseObjectTypesRemoved, "custom Metaverse Object Type", "custom Metaverse Object Types"),
                (r.CustomMetaverseAttributesRemoved, "custom metaverse attribute", "custom metaverse attributes"),
                (r.CustomRolesRemoved, "custom role", "custom roles"),
                (r.CustomConnectorDefinitionsRemoved, "custom connector definition", "custom connector definitions"),
                (r.CustomPredefinedSearchesRemoved, "custom predefined search", "custom predefined searches"),
                (r.CustomExampleDataSetsRemoved, "custom example data set", "custom example data sets"),
                (r.CustomExampleDataTemplatesRemoved, "custom example data template", "custom example data templates"),
                (r.CustomApiKeysRemoved, "custom API key", "custom API keys"),
                (r.TrustedCertificatesRemoved, "trusted certificate", "trusted certificates")
            };

            var bullets = categories
                .Where(c => c.Count > 0)
                .Select(c => $"• {c.Count.ToString("N0", CultureInfo.InvariantCulture)} {(c.Count == 1 ? c.Singular : c.Plural)}")
                .ToList();

            const string newLine = "\n";
            if (bullets.Count == 0)
                return $"{header}{newLine}{newLine}Nothing was removed; the system was already empty.";

            return $"{header}{newLine}{newLine}Removed:{newLine}{string.Join(newLine, bullets)}";
        }
    }
}
