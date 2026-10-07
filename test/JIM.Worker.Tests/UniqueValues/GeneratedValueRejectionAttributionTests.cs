// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Services;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Attribution of a "value already in use" export rejection to the generated value it is about (Unique Value
/// Generation, #242, release 4; plan decision 9 as revised at the release 4 plan review). Attribution never guesses: a
/// named attribute the export carries a generated value for; a named Metaverse-derived attribute whose derivation has
/// exactly one generated input; or, with nothing named, an export carrying exactly one generated value. Anything else
/// is unattributable and stays an ordinary export error.
/// </summary>
[TestFixture]
public class GeneratedValueRejectionAttributionTests
{
    private const int DirectorySystemId = 2;

    private DerivedFlowTestModel _model = null!;
    private SyncRule _hrImport = null!;
    private SyncRule _directoryExport = null!;
    private ConnectedSystemObjectTypeAttribute _sAMAccountName = null!;
    private ConnectedSystemObjectTypeAttribute _userPrincipalName = null!;
    private ConnectedSystemObjectTypeAttribute _mailNickname = null!;
    private ConnectedSystemObjectTypeAttribute _displayName = null!;
    private List<ConnectedSystemObjectTypeAttribute> _directoryAttributes = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        _hrImport = ImportRule(1, "HR Import", connectedSystemId: 1);
        _hrImport.MetaverseObjectType = _model.Person;
        Generated(_hrImport, 100, _model.AccountName, "Lower(cs[\"first\"]) + \".\" + Lower(cs[\"last\"])");
        // User Principal Name is derived from the generated Account Name (#1750).
        Expression(_hrImport, 101, _model.UserPrincipalName, "mv[\"Account Name\"] + \"@corp.local\"");

        _sAMAccountName = new ConnectedSystemObjectTypeAttribute { Id = 500, Name = "sAMAccountName", Type = AttributeDataType.Text };
        _userPrincipalName = new ConnectedSystemObjectTypeAttribute { Id = 501, Name = "userPrincipalName", Type = AttributeDataType.Text };
        _mailNickname = new ConnectedSystemObjectTypeAttribute { Id = 502, Name = "mailNickname", Type = AttributeDataType.Text };
        _displayName = new ConnectedSystemObjectTypeAttribute { Id = 503, Name = "displayName", Type = AttributeDataType.Text };
        _directoryAttributes = [_sAMAccountName, _userPrincipalName, _mailNickname, _displayName];

        _directoryExport = ExportRule(2, "Directory Export", DirectorySystemId);
        _directoryExport.MetaverseObjectType = _model.Person;
        AddExportMapping(_sAMAccountName, _model.AccountName);
        AddExportMapping(_userPrincipalName, _model.UserPrincipalName);
        AddExportMapping(_displayName, _model.DisplayName);
    }

    [Test]
    public void Attribute_NamedAttributeCarryingAGeneratedValue_IsAttributedToIt()
    {
        var export = Export((_sAMAccountName, "joe.bloggs"), (_displayName, "Joe Bloggs"));

        var result = Attribute(export, "sAMAccountName");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.True, result.UnattributableReason);
            Assert.That(result.MetaverseAttributeId, Is.EqualTo(_model.AccountName.Id));
            Assert.That(result.CarryingChange!.AttributeId, Is.EqualTo(_sAMAccountName.Id));
            Assert.That(result.ThroughDerivation, Is.False);
        }
    }

    [Test]
    public void Attribute_NamedAttributeMatchesCaseInsensitively_IsAttributed()
    {
        var export = Export((_sAMAccountName, "joe.bloggs"));

        var result = Attribute(export, "SAMACCOUNTNAME");

        Assert.That(result.IsAttributed, Is.True, result.UnattributableReason);
    }

    [Test]
    public void Attribute_NamedDerivedAttributeWithOneGeneratedInput_IsAttributedToTheGeneratedInput()
    {
        var export = Export((_userPrincipalName, "joe.bloggs@corp.local"));

        var result = Attribute(export, "userPrincipalName");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.True, result.UnattributableReason);
            Assert.That(result.MetaverseAttributeId, Is.EqualTo(_model.AccountName.Id), "the derived value follows the generated value it is built from");
            Assert.That(result.ThroughDerivation, Is.True);
        }
    }

    [Test]
    public void Attribute_NamedDerivedAttributeWithTwoGeneratedInputs_IsUnattributable()
    {
        // Mail Nickname is generated too, and User Principal Name now reads both.
        Generated(_hrImport, 102, _model.MailNickname, "Lower(cs[\"first\"])");
        _hrImport.AttributeFlowRules.Single(m => m.Id == 101).Sources[0].Expression = "mv[\"Account Name\"] + mv[\"Mail Nickname\"]";
        var export = Export((_userPrincipalName, "joe.bloggsjoe"));

        var result = Attribute(export, "userPrincipalName");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.False, "two generated values could have caused it, and attribution never guesses");
            Assert.That(result.UnattributableReason, Is.Not.Null);
        }
    }

    [Test]
    public void Attribute_NothingNamedAndOneGeneratedValueCarried_IsAttributed()
    {
        var export = Export((_sAMAccountName, "joe.bloggs"), (_displayName, "Joe Bloggs"));

        var result = Attribute(export, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.True, result.UnattributableReason);
            Assert.That(result.MetaverseAttributeId, Is.EqualTo(_model.AccountName.Id));
        }
    }

    [Test]
    public void Attribute_NothingNamedAndTheGeneratedValueCarriedDirectlyAndDerived_IsStillOneGeneratedValue()
    {
        var export = Export((_sAMAccountName, "joe.bloggs"), (_userPrincipalName, "joe.bloggs@corp.local"));

        var result = Attribute(export, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.True, result.UnattributableReason);
            Assert.That(result.CarryingChange!.AttributeId, Is.EqualTo(_sAMAccountName.Id), "the attribute carrying the value directly is preferred");
        }
    }

    [Test]
    public void Attribute_NothingNamedAndTwoGeneratedValuesCarried_IsUnattributable()
    {
        Generated(_hrImport, 102, _model.MailNickname, "Lower(cs[\"first\"])");
        AddExportMapping(_mailNickname, _model.MailNickname);
        var export = Export((_sAMAccountName, "joe.bloggs"), (_mailNickname, "joe"));

        var result = Attribute(export, null);

        Assert.That(result.IsAttributed, Is.False);
    }

    [Test]
    public void Attribute_NothingNamedAndNoGeneratedValueCarried_IsUnattributable()
    {
        var export = Export((_displayName, "Joe Bloggs"));

        var result = Attribute(export, null);

        Assert.That(result.IsAttributed, Is.False);
    }

    [Test]
    public void Attribute_NameTheSystemDoesNotRecognise_IsUnattributable()
    {
        var export = Export((_sAMAccountName, "joe.bloggs"));

        var result = Attribute(export, "msDS-SomethingElse");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.False, "an attribute name JIM does not recognise is never matched to a generated value by elimination");
            Assert.That(result.UnattributableReason, Does.Contain("msDS-SomethingElse"));
        }
    }

    [Test]
    public void Attribute_NamedAttributeNotGenerated_IsUnattributable()
    {
        var export = Export((_sAMAccountName, "joe.bloggs"), (_displayName, "Joe Bloggs"));

        var result = Attribute(export, "displayName");

        Assert.That(result.IsAttributed, Is.False);
    }

    [Test]
    public void Attribute_NamedAttributeTheExportDoesNotCarry_IsUnattributable()
    {
        var export = Export((_displayName, "Joe Bloggs"));

        var result = Attribute(export, "sAMAccountName");

        Assert.That(result.IsAttributed, Is.False);
    }

    [Test]
    public void Attribute_ExportModeGeneratedMapping_IsAttributedToTheExportMapping()
    {
        var loginName = new ConnectedSystemObjectTypeAttribute { Id = 600, Name = "loginName", Type = AttributeDataType.Text };
        _directoryAttributes.Add(loginName);
        var generatedExport = new SyncRuleMapping
        {
            Id = 300, SyncRule = _directoryExport, SyncRuleId = _directoryExport.Id,
            TargetConnectedSystemAttribute = loginName, TargetConnectedSystemAttributeId = loginName.Id,
            Generation = new SyncRuleMappingGeneration { Id = 9 }
        };
        generatedExport.Sources.Add(new SyncRuleMappingSource { Expression = "Lower(mv[\"Account Name\"])" });
        _directoryExport.AttributeFlowRules.Add(generatedExport);
        var export = Export((loginName, "joe"));

        var result = Attribute(export, "loginName");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsAttributed, Is.True, result.UnattributableReason);
            Assert.That(result.ExportGeneratedMapping, Is.SameAs(generatedExport));
            Assert.That(result.MetaverseAttributeId, Is.Null);
        }
    }

    // ---- Helpers ----

    private GeneratedValueRejectionAttributionResult Attribute(PendingExport export, string? rejectedAttributeName)
    {
        var graph = DerivedFlowGraphFactory.Create([_hrImport], _model.Types);
        return GeneratedValueRejectionAttribution.Attribute(export, rejectedAttributeName, _directoryAttributes, [_directoryExport], [_hrImport], graph);
    }

    private void AddExportMapping(ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source)
    {
        var mapping = new SyncRuleMapping
        {
            Id = 200 + _directoryExport.AttributeFlowRules.Count,
            SyncRule = _directoryExport,
            SyncRuleId = _directoryExport.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id });
        _directoryExport.AttributeFlowRules.Add(mapping);
    }

    private PendingExport Export(params (ConnectedSystemObjectTypeAttribute Attribute, string Value)[] changes)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = DirectorySystemId,
            ChangeType = PendingExportChangeType.Create,
            SourceMetaverseObjectId = Guid.NewGuid()
        };
        foreach (var (attribute, value) in changes)
        {
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = attribute,
                AttributeId = attribute.Id,
                StringValue = value,
                SyncRuleId = _directoryExport.Id
            });
        }

        return export;
    }
}
