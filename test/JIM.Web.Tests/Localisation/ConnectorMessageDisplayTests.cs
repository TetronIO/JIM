// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// How a finding from validating a Connected System's settings reads in the viewer's language (#3030): the English the
/// REST API and PowerShell read is unchanged, and the portal shows the same finding in French with each setting's name
/// as its label.
/// </summary>
[TestFixture]
public class ConnectorMessageDisplayTests
{
    private const string LdapDefinition = "JIM LDAP Connector";

    private static ConnectedSystemSettingValue Value(string name, bool required) => new()
    {
        Setting = new ConnectorDefinitionSetting
        {
            Name = name,
            Required = required,
            Type = ConnectedSystemSettingType.String
        }
    };

    [Test]
    public void Validate_MissingRequiredValue_KeepsItsEnglish_AndCarriesAMessage()
    {
        var results = ConnectorSettingValidator.Validate([Value("Connection Timeout", required: true)]);

        var result = results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorMessage, Is.EqualTo("Please supply a value for Connection Timeout"));
            Assert.That(result.ErrorDetail, Is.Not.Null);
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void Describe_InFrench_ShowsTheFindingWithTheSettingsLabel()
    {
        var result = ConnectorSettingValidator.Validate([Value("Connection Timeout", required: true)]).Single();

        Assert.That(result.Describe(LdapDefinition), Is.EqualTo("Veuillez indiquer une valeur pour Délai de connexion"));
    }

    [Test]
    [SetUICulture("fr")]
    public void Describe_AGroupFinding_InFrench_RelabelsEverySettingInItsList()
    {
        var members = new List<ConnectedSystemSettingValue> { Value("Host", false), Value("Port", false) };
        members[0].Setting.RequiredGroup = "G";
        members[1].Setting.RequiredGroup = "G";
        var result = ConnectorSettingValueValidationResult.Failure(null, ConnectorSettingValidator.BuildGroupMessage(members));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorMessage, Is.EqualTo("Provide a value for at least one of these settings: 'Host', 'Port'."));
            Assert.That(result.Describe(LdapDefinition), Is.EqualTo("Indiquez une valeur pour au moins un de ces paramètres : 'Hôte', 'Port'."));
        }
    }

    [Test]
    [SetUICulture("en-GB")]
    public void Describe_InEnglish_IsTheEnglishTheApiReads()
    {
        var result = ConnectorSettingValidator.Validate([Value("Connection Timeout", required: true)]).Single();

        Assert.That(result.Describe(LdapDefinition), Is.EqualTo(result.ErrorMessage));
    }

    [Test]
    public void Describe_AFindingWithoutAMessage_IsItsEnglishText()
    {
        var result = new ConnectorSettingValueValidationResult { ErrorMessage = "Another system's words" };

        Assert.That(result.Describe(LdapDefinition), Is.EqualTo("Another system's words"));
    }

    private const string DocumentWithoutAnchor = """{"objectTypes":[{"name":"Person","table":"people"}]}""";

    [Test]
    [SetUICulture("en-GB")]
    public void ObjectTypesProblem_InEnglish_IsTheParsersOwnWords()
    {
        var problem = JIM.Connectors.Sql.SqlObjectTypesDocument.Validate(DocumentWithoutAnchor).First();

        Assert.That(problem.Text(), Is.EqualTo(problem.Message));
    }

    [Test]
    [SetUICulture("fr")]
    public void ObjectTypesProblem_InFrench_IsTheSameProblemInFrench()
    {
        var problem = JIM.Connectors.Sql.SqlObjectTypesDocument.Validate(DocumentWithoutAnchor).First();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Message, Does.StartWith("Object Type 'Person' has no 'anchorColumns'."));
            Assert.That(problem.Text(), Does.StartWith("Le type d’objet « Person » n’a pas de « anchorColumns »."));
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void Describe_AConnectorsFinding_InFrench_NamesTheSettingByItsLabel()
    {
        var message = JimMessage.Create(ConnectorMessageCatalogues.Connectors, "GraphClientIdInvalid", "Client ID");
        var result = ConnectorSettingValueValidationResult.Failure(null, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorMessage, Does.StartWith("The Client ID must be the app registration"));
            Assert.That(result.Describe("JIM Microsoft Entra ID Connector"), Does.StartWith("Le paramètre ID client doit être l’ID d’application"));
        }
    }
}
