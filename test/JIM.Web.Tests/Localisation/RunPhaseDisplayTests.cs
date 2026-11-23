// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Run Profile phase names shown in the portal come from <c>RunPhaseText.resx</c>, looked up by the phase's key (#3030).
/// Every key JIM declares must have a name, and the English must match the stored English so en-GB pages read as before.
/// </summary>
[TestFixture]
public class RunPhaseDisplayTests
{
    [Test]
    public void EveryRunPhaseKey_HasAResourceName()
    {
        var missing = typeof(RunPhaseKeys)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(key => RunPhaseText.ResourceManager.GetString(RunPhaseDisplay.ResourceKeyFor(key), CultureInfo.InvariantCulture) == null)
            .ToList();

        Assert.That(missing, Is.Empty, "RunPhaseText.resx has no name for these phase keys");
    }

    [Test]
    [SetUICulture("en-GB")]
    public void Name_EveryDeclaredPhase_EnglishMatchesTheStoredName()
    {
        var mismatches = Enum.GetValues<ConnectedSystemRunType>()
            .SelectMany(RunProfilePhaseCatalogue.GetPhases)
            .Where(phase => RunPhaseDisplay.Name(phase.Key, "unused") != phase.Name)
            .Select(phase => $"{phase.Key}: '{RunPhaseDisplay.Name(phase.Key, "unused")}' != '{phase.Name}'")
            .ToList();

        Assert.That(mismatches, Is.Empty);
    }

    [Test]
    [SetUICulture("fr")]
    public void Name_FrenchViewer_ReturnsTheFrenchName()
    {
        Assert.That(RunPhaseDisplay.Name(RunPhaseKeys.ImportFetch, "Importing objects"), Is.EqualTo("Importation des objets"));
    }

    [Test]
    [SetUICulture("fr")]
    public void Name_PhaseWithoutAResource_FallsBackToTheStoredEnglish()
    {
        Assert.That(RunPhaseDisplay.Name("connector:custom.step", "Reading the file"), Is.EqualTo("Reading the file"));
    }

    [Test]
    public void ResourceKeyFor_DottedPhaseKey_BecomesUnderscoredSlug()
    {
        Assert.That(RunPhaseDisplay.ResourceKeyFor("import.fetch"), Is.EqualTo("Import_Fetch"));
    }
}
