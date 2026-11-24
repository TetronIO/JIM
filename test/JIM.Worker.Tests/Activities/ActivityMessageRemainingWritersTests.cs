// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core.DTOs;
using JIM.Models.Localisation;
using JIM.Models.Resources;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// The Activity and execution item texts that were English-only after #3039's first pass and are stored as messages now
/// (#3084): the English is exactly what each writer always wrote, and each message renders in French and German.
/// </summary>
[TestFixture]
public class ActivityMessageRemainingWritersTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static string In(JimMessage message, CultureInfo language) => MessageRenderer.Render(message, language, language);

    private static void AssertTranslated(JimMessage message, string english)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(english));
            Assert.That(In(message, French), Is.Not.EqualTo(english));
            Assert.That(In(message, German), Is.Not.EqualTo(english));
        }
    }

    private static AttributeReference Described(JimMessage description) => new() { Kind = AttributeReferenceKind.ImportAttributeFlow, DescriptionDetail = description };

    [Test]
    public void MetaverseRemovals_SayWhatTheServerAlwaysWrote_AndTranslate()
    {
        var reference = Described(JimMessage.Create(MessageCatalogues.MetaverseReferences, nameof(MetaverseReferenceMessages.ReferenceImportFlowInRule), "HR to Metaverse"));

        AssertTranslated(ActivityMessage.MetaverseAttributeReferenceRemoved(reference, "Mail"),
            "Removed Import Attribute Flow in Synchronisation Rule 'HR to Metaverse' referencing Metaverse Attribute 'Mail'");
        AssertTranslated(ActivityMessage.MetaverseAttributeRemoved("Mail"), "Removed Metaverse Attribute 'Mail'");
        AssertTranslated(ActivityMessage.MetaverseAttributeUnassigned("Mail", "User"),
            "Unassigned Metaverse Attribute 'Mail' from Metaverse Object Type 'User'");
        AssertTranslated(ActivityMessage.MetaverseReferenceRemovedScoped(ActivityMessage.DescribedReference(reference), "User"),
            "Removed Import Attribute Flow in Synchronisation Rule 'HR to Metaverse' scoped to Metaverse Object Type 'User'");
        AssertTranslated(ActivityMessage.MetaverseObjectTypeRemoved("User"), "Removed Metaverse Object Type 'User'");
    }

    [Test]
    public void MetaverseReference_DescribedByEnglishOnly_StaysAsWritten()
    {
        var reference = new AttributeReference { Description = "Object Type binding: User" };

        Assert.That(ActivityMessage.MetaverseAttributeReferenceRemoved(reference, "Mail").ToEnglish(),
            Is.EqualTo("Removed Object Type binding: User referencing Metaverse Attribute 'Mail'"));
    }

    [Test]
    public void AttributeReference_SettingTheDescriptionMessage_SetsTheEnglishDescription()
    {
        var reference = Described(JimMessage.Create(MessageCatalogues.MetaverseReferences, nameof(MetaverseReferenceMessages.ReferenceSsoMapping)));

        Assert.That(reference.Description, Is.EqualTo("Service Settings SSO unique-identifier mapping (cleared)"));
    }

    [Test]
    public void MetaverseReferenceDescriptions_EachSaysWhatTheRepositoryAlwaysWrote_AndTranslates()
    {
        JimMessage Make(string key, params MessageArgument[] arguments) => JimMessage.Create(MessageCatalogues.MetaverseReferences, key, arguments);

        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceBinding), "User"), "Object Type binding: User");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceImportFlowInRule), "R"), "Import Attribute Flow in Synchronisation Rule 'R'");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceImportFlowMapping), "4"), "Import Attribute Flow (mapping 4)");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportSourceInRule), "R"), "Export Attribute Flow source in Synchronisation Rule 'R'");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportSourceMapping), "4"), "Export Attribute Flow source (mapping source 4)");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportMappingInRuleRemoved), "R"),
            "Export Attribute Flow in Synchronisation Rule 'R' (removed: it would be left with no sources)");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportMappingRemoved), "4"),
            "Export Attribute Flow (mapping 4) removed: it would be left with no sources");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceScopingCriterion), "4"), "Scoping criterion 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceObjectMatchingRule), "4"), "Object Matching Rule 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferencePredefinedSearchColumn), "S"), "Predefined Search column in 'S'");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferencePredefinedSearchCriterion), "4"), "Predefined Search criterion 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExampleDataAttribute), "4"), "Example Data template attribute 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExampleDataDependency), "4"), "Example Data template attribute dependency 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceSsoMapping)), "Service Settings SSO unique-identifier mapping (cleared)");
    }
}
