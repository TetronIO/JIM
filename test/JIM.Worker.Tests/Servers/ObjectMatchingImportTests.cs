// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.InMemoryData;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Import join through <c>ObjectMatchingServer</c> (#2052): each rule's values come from the one match-value pipeline,
/// every value is looked up in one query, and nothing is decided quietly. A rule that cannot be worked out stops the
/// object; values that together find more than one identity are ambiguous; and only a rule with no value lets the next
/// rule be tried.
/// </summary>
public class ObjectMatchingImportTests
{
    private static readonly MetaverseObjectType Person = new() { Id = 1, Name = "Person", PluralName = "People" };
    private static readonly MetaverseAttribute EmployeeId = new() { Id = 100, Name = "Employee ID", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Email = new() { Id = 101, Name = "Email", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute EmployeeNumber = new() { Id = 10, Name = "employeeNumber", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute Mail = new() { Id = 11, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };

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

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_ExpressionRule_JoinsOnTheReshapedValueAsync()
    {
        var identity = SeedMvo((EmployeeId, "48213"));
        var cso = Cso((EmployeeNumber, "EMP-48213"));
        var rule = ExpressionRule("Replace(cs[\"employeeNumber\"], \"EMP-\", \"\")", EmployeeId);

        var match = await _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, [rule]);

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.MetaverseObject.Id, Is.EqualTo(identity.Id));
        Assert.That(match.Rule, Is.SameAs(rule));
        Assert.That(match.Resolution.Values, Is.EqualTo(new object[] { "48213" }));
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_IgnoringCase_JoinsAValueInAnotherCaseAsync()
    {
        var identity = SeedMvo((EmployeeId, "JBloggs"));

        var match = await _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(Cso((EmployeeNumber, "jbloggs")), [AttributeRule(EmployeeNumber, EmployeeId)]);

        Assert.That(match?.MetaverseObject.Id, Is.EqualTo(identity.Id));
    }

    [Test]
    public void FindMatchingMetaverseObjectAsync_RuleThatCannotBeWorkedOut_StopsTheObjectAsync()
    {
        // Previously logged and skipped, so the object matched nothing and projected a duplicate identity.
        SeedMvo((EmployeeId, "48213"));
        var failing = ExpressionRule("Trim(cs[\"employeeNumber\"]", EmployeeId, order: 0);
        var fallback = AttributeRule(EmployeeNumber, EmployeeId, order: 1);

        var ex = Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() =>
            _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(Cso((EmployeeNumber, "48213")), [failing, fallback]));

        Assert.That(ex!.Rule, Is.SameAs(failing), "a later rule must not be tried once a rule has failed");
        Assert.That(ex.Message, Does.Contain("Employee ID"));
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_RuleWithNoValue_TriesTheNextRuleAsync()
    {
        var identity = SeedMvo((Email, "jo@corp.example"));
        var noValue = AttributeRule(EmployeeNumber, EmployeeId, order: 0);
        var byMail = AttributeRule(Mail, Email, order: 1);

        var match = await _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(Cso((Mail, "jo@corp.example")), [noValue, byMail]);

        Assert.That(match?.MetaverseObject.Id, Is.EqualTo(identity.Id));
        Assert.That(match!.Rule, Is.SameAs(byMail));
    }

    [Test]
    public void FindMatchingMetaverseObjectAsync_ValuesFindingTwoIdentities_IsAmbiguousAsync()
    {
        // Previously the first value that found exactly one identity won, so which identity joined depended on the
        // order the values happened to load in.
        var first = SeedMvo((Email, "a@corp.example"));
        var second = SeedMvo((Email, "b@corp.example"));
        var rule = AttributeRule(Mail, Email);

        var ex = Assert.ThrowsAsync<MultipleMatchesException>(() =>
            _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(Cso((Mail, "a@corp.example"), (Mail, "b@corp.example")), [rule]));

        Assert.That(ex!.Matches, Is.EquivalentTo(new[] { first.Id, second.Id }));
        Assert.That(ex.Rule, Is.SameAs(rule));
        Assert.That(ex.MatchValues, Is.EquivalentTo(new object[] { "a@corp.example", "b@corp.example" }));
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_ValuesFindingOneIdentity_JoinsItAsync()
    {
        var identity = SeedMvo((Email, "a@corp.example"), (Email, "b@corp.example"));

        var match = await _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(
            Cso((Mail, "a@corp.example"), (Mail, "b@corp.example")), [AttributeRule(Mail, Email)]);

        Assert.That(match?.MetaverseObject.Id, Is.EqualTo(identity.Id));
    }

    private MetaverseObject SeedMvo(params (MetaverseAttribute Attribute, string Value)[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = Person };
        foreach (var (attribute, value) in values)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = attribute, AttributeId = attribute.Id, StringValue = value });
        _syncRepo.SeedMetaverseObject(mvo);
        return mvo;
    }

    private static ConnectedSystemObject Cso(params (ConnectedSystemObjectTypeAttribute Attribute, string Value)[] values)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        foreach (var (attribute, value) in values)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = attribute.Id, Attribute = attribute, StringValue = value });
        return cso;
    }

    private static ObjectMatchingRule AttributeRule(ConnectedSystemObjectTypeAttribute source, MetaverseAttribute target, int order = 0) => new()
    {
        Id = 1 + order,
        Order = order,
        MetaverseObjectType = Person,
        MetaverseObjectTypeId = Person.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = [new ObjectMatchingRuleSource { ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id }]
    };

    private static ObjectMatchingRule ExpressionRule(string expression, MetaverseAttribute target, int order = 0) => new()
    {
        Id = 1 + order,
        Order = order,
        MetaverseObjectType = Person,
        MetaverseObjectTypeId = Person.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = [new ObjectMatchingRuleSource { Expression = expression }]
    };
}
