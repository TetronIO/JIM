// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Application.Services;
using JIM.Models.Core.DTOs;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// How the Connected System server's refusals and explanations read in the viewer's language (#3030): the English the
/// REST API, PowerShell and logs read is unchanged, and the portal shows the same text in French.
/// </summary>
[TestFixture]
public class ConnectedSystemMessageDisplayTests
{
    [Test]
    public void LocalisedArgument_KeepsItsEnglishMessage_ParameterNameIncluded()
    {
        var exception = LocalisedExceptions.Argument(
            ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ConnectedSystemNotFoundById), 7), "connectedSystemId");

        Assert.That(exception.GetType(), Is.EqualTo(typeof(ArgumentException)));
        Assert.That(exception.Message, Does.StartWith("No Connected System found with id 7."));
        Assert.That(exception.Message, Does.Contain("connectedSystemId"));
    }

    [Test]
    [SetUICulture("fr")]
    public void ToDisplayText_AMessageCarryingException_InFrench_ShowsTheFrench()
    {
        var exception = LocalisedExceptions.Argument(
            ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ConnectedSystemNotFoundById), 7), "connectedSystemId");

        Assert.That(exception.ToDisplayText(), Is.EqualTo("Aucun système connecté trouvé avec l’ID 7."));
    }

    [Test]
    [SetUICulture("fr")]
    public void ToDisplayText_AnOrdinaryException_ShowsItsOwnMessage()
    {
        Assert.That(new InvalidOperationException("Boom").ToDisplayText(), Is.EqualTo("Boom"));
    }

    [Test]
    public void ObjectTypeStillManaged_KeepsItsEnglish_ForOneRuleAndSeveral()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ObjectTypeDeselectionMessages.StillManaged("user", ["Rule A"]),
                Is.EqualTo("Object Type 'user' cannot be deselected while an enabled Synchronisation Rule still manages it: Rule A. " +
                           "Disable that Synchronisation Rule first, or keep the Object Type selected."));
            Assert.That(ObjectTypeDeselectionMessages.StillManaged("user", ["Rule A", "Rule B"]),
                Is.EqualTo("Object Type 'user' cannot be deselected while 2 enabled Synchronisation Rules still manage it: Rule A, Rule B. " +
                           "Disable those Synchronisation Rules first, or keep the Object Type selected."));
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void ObjectTypeStillManaged_InFrench_UsesTheCountsPluralForm()
    {
        var text = ObjectTypeDeselectionMessages.StillManagedMessage("user", ["Rule A", "Rule B"]).Show();

        Assert.That(text, Does.StartWith("Le type d’objets « user » ne peut pas être désélectionné tant que 2 règles de synchronisation activées"));
    }

    [Test]
    public void AttributeReadByConfigurationException_KeepsItsEnglish_AndCarriesAMessage()
    {
        var reader = new AttributeReader
        {
            Description = "Scoping Criterion on 'department' in Synchronisation Rule 'HR'",
            DescriptionDetail = ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderScopingCriterionAttribute), "department",
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderOwnerSyncRule), "HR"))
        };
        var exception = new AttributeReadByConfigurationException(
            ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.SchemaRefreshRemovalRefusal)), [reader]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo(
                "Apply and Remove is refused: configuration reads an attribute the Connected System no longer reports. " +
                "Change these first, or apply without removing: Scoping Criterion on 'department' in Synchronisation Rule 'HR'."));
            Assert.That(exception.Detail, Is.Not.Null);
            Assert.That(reader.DescriptionDetail!.ToEnglish(), Is.EqualTo(reader.Description));
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void AttributeReader_InFrench_DescribesTheReaderWithTheGlossaryTerms()
    {
        var reader = new AttributeReader
        {
            Description = "Scoping Criterion on 'department' in Synchronisation Rule 'HR'",
            DescriptionDetail = ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderScopingCriterionAttribute), "department",
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderOwnerSyncRule), "HR"))
        };

        Assert.That(reader.ShowDescription(), Is.EqualTo("critère de périmètre sur « department » dans règle de synchronisation « HR »"));
    }

    [Test]
    public void UnresolvedReferenceClassifier_KeepsItsEnglish_AndCarriesTheMessages()
    {
        var classifier = new UnresolvedReferenceClassifier(new ConnectedSystem { Name = "HR" }, containment: null);

        var diagnosis = classifier.Diagnose("manager", "CN=Someone");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Finding, Is.EqualTo("Nothing with this value has been imported into HR."));
            Assert.That(diagnosis.Remedy, Does.StartWith("Run a Full Import to rule out a missed change."));
            Assert.That(diagnosis.FindingDetail!.ToEnglish(), Is.EqualTo(diagnosis.Finding));
            Assert.That(diagnosis.RemedyDetail!.ToEnglish(), Is.EqualTo(diagnosis.Remedy));
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void UnresolvedReferenceClassifier_InFrench_ShowsTheFinding()
    {
        var classifier = new UnresolvedReferenceClassifier(new ConnectedSystem { Name = "HR" }, containment: null);

        Assert.That(classifier.Diagnose("manager", "CN=Someone").ShowFinding(),
            Is.EqualTo("Aucun objet ayant cette valeur n’a été importé dans HR."));
    }

    [Test]
    public void RelativeDateResolver_Describe_KeepsItsEnglish()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RelativeDateResolver.Describe(1, RelativeDateUnit.Days, RelativeDateDirection.Ago), Is.EqualTo("1 day ago"));
            Assert.That(RelativeDateResolver.Describe(30, RelativeDateUnit.Days, RelativeDateDirection.Ago), Is.EqualTo("30 days ago"));
            Assert.That(RelativeDateResolver.Describe(7, RelativeDateUnit.Weeks, RelativeDateDirection.FromNow), Is.EqualTo("7 weeks from now"));
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void RelativeDateResolver_DescribeMessage_InFrench_PutsTheDirectionBeforeTheCount()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RelativeDateResolver.DescribeMessage(30, RelativeDateUnit.Days, RelativeDateDirection.Ago).Show(), Is.EqualTo("il y a 30 jours"));
            Assert.That(RelativeDateResolver.DescribeMessage(1, RelativeDateUnit.Days, RelativeDateDirection.FromNow).Show(), Is.EqualTo("dans 1 jour"));
        }
    }

    [Test]
    public void ScopingCriterion_ToString_KeepsItsEnglish()
    {
        var criterion = new SyncRuleScopingCriteria
        {
            MetaverseAttribute = new JIM.Models.Core.MetaverseAttribute { Name = "Department", Type = JIM.Models.Core.AttributeDataType.Text },
            StringValue = "Sales"
        };

        Assert.That(criterion.ToString(), Is.EqualTo("Text: Sales"));
    }

    [Test]
    [SetUICulture("fr")]
    public void ScopingCriterion_Describe_InFrench_LabelsTheValue()
    {
        var criterion = new SyncRuleScopingCriteria
        {
            MetaverseAttribute = new JIM.Models.Core.MetaverseAttribute { Name = "Department", Type = JIM.Models.Core.AttributeDataType.Text },
            StringValue = "Sales"
        };

        Assert.That(criterion.Describe().Show(), Is.EqualTo("Texte : Sales"));
    }

    [Test]
    public void SyncRule_SaveWarning_KeepsItsEnglish_AndFindsItsMessage()
    {
        var rule = new SyncRule { Name = "Export" };
        var warning = ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.SaveWarningThisObjectType), "user");

        rule.AddRuleSaveWarning(warning);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rule.SaveWarnings, Is.EqualTo(new[] { "'user'" }));
            Assert.That(rule.DescribeSaveWarning("'user'"), Is.EqualTo(warning));
            Assert.That(rule.DescribeSaveWarning("something else"), Is.Null);
        }
    }
}
