// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.InMemoryData;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Object Matching tester (#2055): sample values typed from the schema, several values for a multi-valued
/// attribute, the values the rule would compare as the match-value pipeline works them out, and optionally the lookup
/// an import join runs against the Metaverse: would join, already joined, joined to another, no match, or ambiguous.
/// </summary>
public class ObjectMatchingTesterTests
{
    private static readonly MetaverseObjectType Person = new() { Id = 1, Name = "Person", PluralName = "People" };
    private static readonly MetaverseAttribute EmployeeId = new() { Id = 100, Name = "Employee ID", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Email = new() { Id = 101, Name = "Email", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute EmployeeNumber = new() { Id = 10, Name = "employeeNumber", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute Mail = new() { Id = 11, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
    private static readonly ConnectedSystemObjectTypeAttribute HireDate = new() { Id = 12, Name = "hireDate", Type = AttributeDataType.DateTime };
    private const int ConnectedSystemId = 5;

    private static readonly ConnectedSystemObjectType PersonType = new()
    {
        Id = 3, Name = "person", ConnectedSystemId = ConnectedSystemId, Attributes = [EmployeeNumber, Mail, HireDate]
    };

    private SyncRepository _syncRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        var context = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(context);
        _syncRepo = TestUtilities.CreateSyncRepository();
        _jim = new JimApplication(new PostgresDataRepository(context.Object), syncRepository: _syncRepo);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Inputs

    [Test]
    public void DescribeTestInputs_AttributeRule_IsItsSourceAttribute()
    {
        var inputs = _jim.ObjectMatching.DescribeTestInputs(AttributeRule(Mail, Email), PersonType);

        Assert.That(inputs, Is.EqualTo(new[] { new AttributeSampleDescriptor("mail", AttributeDataType.Text, AttributePlurality.MultiValued) }));
    }

    [Test]
    public void DescribeTestInputs_ExpressionRule_IsEachSchemaAttributeItReads()
    {
        var inputs = _jim.ObjectMatching.DescribeTestInputs(
            ExpressionRule("cs[\"employeeNumber\"] + FormatDate((DateTime)cs[\"hireDate\"], \"yyyy\") + cs[\"unknown\"]", EmployeeId), PersonType);

        Assert.That(inputs.Select(i => i.AttributeName), Is.EqualTo(new[] { "employeeNumber", "hireDate" }));
    }

    [Test]
    public void TestInputsFromConnectedSystemObject_TakesEveryValueOfEachInput()
    {
        var cso = Cso((Mail, "a@corp.example"), (Mail, "b@corp.example"), (EmployeeNumber, "48213"));

        var inputs = _jim.ObjectMatching.TestInputsFromConnectedSystemObject(cso, AttributeRule(Mail, Email), PersonType);

        Assert.That(inputs.Single().Values, Is.EqualTo(new object[] { "a@corp.example", "b@corp.example" }));
    }

    #endregion

    #region Match values

    [Test]
    public async Task TestAsync_TypedSample_ReachesTheExpressionAsItsTypeAsync()
    {
        var rule = ExpressionRule("cs[\"employeeNumber\"] + \"-\" + FormatDate((DateTime)cs[\"hireDate\"], \"yyyy\")", EmployeeId);

        var result = await _jim.ObjectMatching.TestAsync(rule, PersonType,
            [new AttributeSample("employeeNumber", ["48213"]), new AttributeSample("hireDate", ["2026-03-02"])], lookUp: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.InputErrors, Is.Empty);
            Assert.That(result.Resolution!.Kind, Is.EqualTo(MatchValueResolutionKind.Values));
            Assert.That(result.Resolution.Values, Is.EqualTo(new object[] { "48213-2026" }));
            Assert.That(result.Lookup, Is.Null, "the lookup was not asked for");
        }
    }

    [Test]
    public async Task TestAsync_SeveralValuesForAMultiValuedAttribute_CompareEachAsync()
    {
        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(Mail, Email), PersonType,
            [new AttributeSample("mail", ["a@corp.example", "b@corp.example"])], lookUp: false);

        Assert.That(result.Resolution!.Values, Is.EquivalentTo(new object[] { "a@corp.example", "b@corp.example" }));
    }

    [Test]
    public async Task TestAsync_NoValue_IsTheNoValueResolutionAsync()
    {
        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(EmployeeNumber, EmployeeId), PersonType,
            [new AttributeSample("employeeNumber", [])], lookUp: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resolution!.Kind, Is.EqualTo(MatchValueResolutionKind.NoValue));
            Assert.That(result.Lookup, Is.Null, "there is nothing to look up");
        }
    }

    [Test]
    public async Task TestAsync_UnconvertibleSample_ReportsAnInputErrorAsync()
    {
        var result = await _jim.ObjectMatching.TestAsync(ExpressionRule("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy\")", EmployeeId),
            PersonType, [new AttributeSample("hireDate", ["soon"])], lookUp: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resolution, Is.Null);
            Assert.That(result.InputErrors.Single(), Does.Contain("soon").And.Contain("hireDate"));
        }
    }

    [Test]
    public async Task TestAsync_SeveralValuesForASingleValuedAttribute_ReportsAnInputErrorAsync()
    {
        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(EmployeeNumber, EmployeeId), PersonType,
            [new AttributeSample("employeeNumber", ["1", "2"])], lookUp: false);

        Assert.That(result.InputErrors.Single(), Does.Contain("single-valued"));
    }

    #endregion

    #region Lookup

    [Test]
    public async Task TestAsync_LookupFindingOneIdentity_WouldJoinItAsync()
    {
        var identity = SeedMvo("Jo Bloggs", (EmployeeId, "48213"));

        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(EmployeeNumber, EmployeeId), PersonType,
            [new AttributeSample("employeeNumber", ["48213"])], lookUp: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.WouldJoin));
            Assert.That(result.Lookup.MetaverseObjectId, Is.EqualTo(identity.Id));
            Assert.That(result.Lookup.Description, Does.Contain("join").And.Contain("Jo Bloggs"));
        }
    }

    [Test]
    public async Task TestAsync_LookupFindingNothing_IsNoMatchAsync()
    {
        SeedMvo("Jo Bloggs", (EmployeeId, "48213"));

        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(EmployeeNumber, EmployeeId), PersonType,
            [new AttributeSample("employeeNumber", ["99999"])], lookUp: true);

        Assert.That(result.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.NoMatch));
    }

    [Test]
    public async Task TestAsync_LookupFindingTwoIdentities_IsAmbiguousWithTheCandidatesAsync()
    {
        var first = SeedMvo("Alex A", (Email, "a@corp.example"));
        var second = SeedMvo("Alex B", (Email, "b@corp.example"));

        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(Mail, Email), PersonType,
            [new AttributeSample("mail", ["a@corp.example", "b@corp.example"])], lookUp: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.Ambiguous));
            Assert.That(result.Lookup.Candidates.Select(c => c.CandidateId), Is.EquivalentTo(new[] { first.Id, second.Id }));
            Assert.That(result.Lookup.Description, Does.Contain("would neither join nor project"));
        }
    }

    [Test]
    public async Task TestAsync_IdentityAlreadyJoinedFromThisConnectedSystem_IsJoinedToAnotherAsync()
    {
        var identity = SeedMvo("Jo Bloggs", (EmployeeId, "48213"));
        _syncRepo.SeedConnectedSystemObject(new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId, TypeId = PersonType.Id, MetaverseObjectId = identity.Id, MetaverseObject = identity
        });

        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(EmployeeNumber, EmployeeId), PersonType,
            [new AttributeSample("employeeNumber", ["48213"])], lookUp: true);

        Assert.That(result.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.JoinedToAnother));
    }

    [Test]
    public async Task TestAsync_TestedObjectAlreadyJoinedToTheIdentity_IsAlreadyJoinedAsync()
    {
        var identity = SeedMvo("Jo Bloggs", (EmployeeId, "48213"));
        var tested = Cso((EmployeeNumber, "48213"));
        tested.ConnectedSystemId = ConnectedSystemId;
        tested.MetaverseObjectId = identity.Id;
        _syncRepo.SeedConnectedSystemObject(tested);

        var result = await _jim.ObjectMatching.TestAsync(AttributeRule(EmployeeNumber, EmployeeId), PersonType,
            [new AttributeSample("employeeNumber", ["48213"])], lookUp: true, testedObject: tested);

        Assert.That(result.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.AlreadyJoined));
    }

    #endregion

    private MetaverseObject SeedMvo(string name, params (MetaverseAttribute Attribute, string Value)[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = Person };
        var displayName = new MetaverseAttribute { Id = 1, Name = "Display Name", Type = AttributeDataType.Text };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = displayName, AttributeId = displayName.Id, StringValue = name });
        foreach (var (attribute, value) in values)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = attribute, AttributeId = attribute.Id, StringValue = value });
        _syncRepo.SeedMetaverseObject(mvo);
        return mvo;
    }

    private static ConnectedSystemObject Cso(params (ConnectedSystemObjectTypeAttribute Attribute, string Value)[] values)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = PersonType.Id };
        foreach (var (attribute, value) in values)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = attribute.Id, Attribute = attribute, StringValue = value });
        return cso;
    }

    private static ObjectMatchingRule AttributeRule(ConnectedSystemObjectTypeAttribute source, MetaverseAttribute target) => new()
    {
        Id = 1,
        MetaverseObjectType = Person,
        MetaverseObjectTypeId = Person.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = [new ObjectMatchingRuleSource { ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id }]
    };

    private static ObjectMatchingRule ExpressionRule(string expression, MetaverseAttribute target) => new()
    {
        Id = 1,
        MetaverseObjectType = Person,
        MetaverseObjectTypeId = Person.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = [new ObjectMatchingRuleSource { Expression = expression }]
    };
}
