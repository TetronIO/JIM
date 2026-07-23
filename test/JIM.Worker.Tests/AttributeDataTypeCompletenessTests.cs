// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Utilities;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests;

/// <summary>
/// Structural guard for AttributeDataType dispatch completeness. JIM dispatches on the attribute
/// data type in many hand-maintained switches; historically, adding a new member (LongNumber, then
/// Decimal) left silent gaps because nothing forced every dispatch site to gain an arm. These tests
/// iterate every value-bearing enum member through the shared dispatch entry points, so a member
/// missing from any of them fails a test here instead of degrading silently at a customer.
///
/// When AttributeDataType gains a new member, the canary test below fails by design. Do not just
/// extend the list: audit every dispatch site first (grep for "case AttributeDataType", switch
/// expressions on the type, and value-carrier if-chains over StringValue/IntValue/LongValue/
/// DecimalValue/DateTimeValue/ByteValue/GuidValue/BoolValue/ReferenceValue), then add the member to
/// the value factories in this fixture so it is exercised everywhere below.
/// </summary>
public class AttributeDataTypeCompletenessTests
{
    /// <summary>
    /// Every member that carries a value. NotSet is deliberately excluded: it carries none and the
    /// dispatch sites reject or ignore it explicitly.
    /// </summary>
    private static readonly AttributeDataType[] ValueBearingTypes =
    {
        AttributeDataType.Text,
        AttributeDataType.Number,
        AttributeDataType.DateTime,
        AttributeDataType.Binary,
        AttributeDataType.Reference,
        AttributeDataType.Guid,
        AttributeDataType.Boolean,
        AttributeDataType.LongNumber,
        AttributeDataType.Decimal
    };

    /// <summary>
    /// The types supported by search and scoping criteria. Binary, Reference and NotSet are a
    /// deliberate, documented exclusion (no operators, rejected at the API boundary).
    /// </summary>
    private static readonly AttributeDataType[] CriteriaSupportedTypes =
    {
        AttributeDataType.Text,
        AttributeDataType.Number,
        AttributeDataType.LongNumber,
        AttributeDataType.Decimal,
        AttributeDataType.DateTime,
        AttributeDataType.Boolean,
        AttributeDataType.Guid
    };

    private static readonly DateTime SampleDateTime = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SampleGuid = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SampleReferenceId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly byte[] SampleBytes = { 1, 2, 3 };

    [Test]
    public void AttributeDataType_MembersMatchGuardedList_NewMembersMustExtendTheseTests()
    {
        var expected = new[] { AttributeDataType.NotSet }.Concat(ValueBearingTypes).OrderBy(t => (int)t);
        Assert.That(Enum.GetValues<AttributeDataType>().OrderBy(t => (int)t), Is.EqualTo(expected),
            "AttributeDataType has gained (or lost) a member. Before extending this list, audit every " +
            "dispatch site in the codebase (see this fixture's XML doc for the grep patterns), add the " +
            "member to each, then add it to the value factories here so every guard test exercises it.");
    }

    /// <summary>
    /// Builds an MVO attribute value carrying a representative value for the given type.
    /// </summary>
    private static MetaverseObjectAttributeValue CreateMvoValue(MetaverseAttribute attribute)
    {
        var value = new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id };
        switch (attribute.Type)
        {
            case AttributeDataType.Text: value.StringValue = "sample"; break;
            case AttributeDataType.Number: value.IntValue = 42; break;
            case AttributeDataType.LongNumber: value.LongValue = 9999999999L; break;
            case AttributeDataType.Decimal: value.DecimalValue = 1.5m; break;
            case AttributeDataType.DateTime: value.DateTimeValue = SampleDateTime; break;
            case AttributeDataType.Binary: value.ByteValue = SampleBytes; break;
            case AttributeDataType.Guid: value.GuidValue = SampleGuid; break;
            case AttributeDataType.Boolean: value.BoolValue = true; break;
            case AttributeDataType.Reference: value.ReferenceValueId = SampleReferenceId; break;
            default: throw new ArgumentOutOfRangeException(nameof(attribute), attribute.Type, "Extend CreateMvoValue for the new member.");
        }
        return value;
    }

    /// <summary>
    /// Builds a CSO attribute value carrying a representative value for the given type.
    /// </summary>
    private static ConnectedSystemObjectAttributeValue CreateCsoValue(AttributeDataType type)
    {
        var value = new ConnectedSystemObjectAttributeValue();
        switch (type)
        {
            case AttributeDataType.Text: value.StringValue = "sample"; break;
            case AttributeDataType.Number: value.IntValue = 42; break;
            case AttributeDataType.LongNumber: value.LongValue = 9999999999L; break;
            case AttributeDataType.Decimal: value.DecimalValue = 1.5m; break;
            case AttributeDataType.DateTime: value.DateTimeValue = SampleDateTime; break;
            case AttributeDataType.Binary: value.ByteValue = SampleBytes; break;
            case AttributeDataType.Guid: value.GuidValue = SampleGuid; break;
            case AttributeDataType.Boolean: value.BoolValue = true; break;
            case AttributeDataType.Reference: value.UnresolvedReferenceValue = "CN=ref,DC=example,DC=com"; break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "Extend CreateCsoValue for the new member.");
        }
        return value;
    }

    /// <summary>
    /// Builds a Pending Export attribute value change carrying a representative value matching
    /// <see cref="CreateCsoValue"/> for the given type.
    /// </summary>
    private static PendingExportAttributeValueChange CreatePendingChange(AttributeDataType type)
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = 1,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "attr", Type = type }
        };
        switch (type)
        {
            case AttributeDataType.Text: change.StringValue = "sample"; break;
            case AttributeDataType.Number: change.IntValue = 42; break;
            case AttributeDataType.LongNumber: change.LongValue = 9999999999L; break;
            case AttributeDataType.Decimal: change.DecimalValue = 1.5m; break;
            case AttributeDataType.DateTime: change.DateTimeValue = SampleDateTime; break;
            case AttributeDataType.Binary: change.ByteValue = SampleBytes; break;
            case AttributeDataType.Guid: change.GuidValue = SampleGuid; break;
            case AttributeDataType.Boolean: change.BoolValue = true; break;
            case AttributeDataType.Reference: change.UnresolvedReferenceValue = "CN=ref,DC=example,DC=com"; break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "Extend CreatePendingChange for the new member.");
        }
        return change;
    }

    private static MetaverseObject BuildMvoWithValue(AttributeDataType type)
    {
        var attribute = new MetaverseAttribute { Id = 1, Name = "attr", Type = type };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        mvo.AttributeValues.Add(CreateMvoValue(attribute));
        return mvo;
    }

    [TestCaseSource(nameof(ValueBearingTypes))]
    public void ExportEvaluationServer_BuildAttributeDictionary_ValueBearingType_ExposesValue(AttributeDataType type)
    {
        // Arrange
        TestUtilities.SetEnvironmentVariables();
        var syncRepo = TestUtilities.CreateSyncRepository();
        using var jim = new JimApplication(new PostgresDataRepository(new Mock<JimDbContext>().Object), syncRepository: syncRepo);
        var server = new ExportEvaluationServer(jim, syncRepo);

        // Act
        var dictionary = server.BuildAttributeDictionary(BuildMvoWithValue(type));

        // Assert - a missing switch arm presents the attribute as null inside export expressions
        Assert.That(dictionary["attr"], Is.Not.Null,
            $"{type} must be exposed to export expression evaluation; a null here means a missing dispatch arm.");
    }

    [TestCaseSource(nameof(ValueBearingTypes))]
    public void DriftDetectionService_BuildAttributeDictionary_ValueBearingType_ExposesValue(AttributeDataType type)
    {
        // Act
        var dictionary = DriftDetectionService.BuildAttributeDictionary(BuildMvoWithValue(type));

        // Assert
        Assert.That(dictionary["attr"], Is.Not.Null,
            $"{type} must be exposed to Drift Detection expression evaluation; a null here means a missing dispatch arm.");
    }

    [TestCaseSource(nameof(ValueBearingTypes))]
    public void SyncEngine_ValueExistsOnCso_MatchingValue_ReturnsTrue(AttributeDataType type)
    {
        // Arrange
        var csoValues = new List<ConnectedSystemObjectAttributeValue> { CreateCsoValue(type) };
        var pendingChange = CreatePendingChange(type);

        // Act / Assert - a missing arm returns false, causing already-current exports to re-export forever
        Assert.That(SyncEngine.ValueExistsOnCso(csoValues, pendingChange), Is.True,
            $"{type} must be comparable during export reconciliation; false for an identical value means a missing dispatch arm.");
    }

    [TestCaseSource(nameof(ValueBearingTypes))]
    public void SyncEngine_GetExpectedValueAsString_ValueBearingType_RendersValue(AttributeDataType type)
    {
        // Act
        var result = SyncEngine.GetExpectedValueAsString(CreatePendingChange(type));

        // Assert
        Assert.That(result, Is.Not.EqualTo("(unknown type)").And.Not.EqualTo("(null)"),
            $"{type} must render a diagnostic value string for reconciliation logging.");
    }

    [TestCaseSource(nameof(ValueBearingTypes))]
    public void SyncEngine_GetImportedValueAsString_ValueBearingType_RendersValue(AttributeDataType type)
    {
        // Arrange
        var csoValuesByAttrId = new Dictionary<int, List<ConnectedSystemObjectAttributeValue>>
        {
            [1] = new() { CreateCsoValue(type) }
        };

        // Act
        var result = SyncEngine.GetImportedValueAsString(csoValuesByAttrId, CreatePendingChange(type));

        // Assert
        Assert.That(result, Is.Not.EqualTo("(no matching type values)"),
            $"{type} must render a diagnostic value string for reconciliation logging.");
    }

    [TestCaseSource(nameof(ValueBearingTypes))]
    public void MetaverseObjectChange_AddAttributeValueChange_ValueBearingType_RecordsValueChange(AttributeDataType type)
    {
        // Arrange
        var attribute = new MetaverseAttribute { Id = 1, Name = "attr", Type = type };
        var attributeValue = CreateMvoValue(attribute);
        if (type == AttributeDataType.LongNumber)
            attributeValue.LongValue = 42L; // stay within int range until #871 gives change history long storage
        if (type == AttributeDataType.Reference)
        {
            // the change recorder tracks resolved references; the FK-only shape is covered too, but the
            // canonical path carries the navigation
            attributeValue.ReferenceValue = new MetaverseObject { Id = SampleReferenceId, Type = new MetaverseObjectType { Id = 1, Name = "User" } };
        }
        var change = new MetaverseObjectChange();

        // Act
        change.AddAttributeValueChange(attributeValue, ValueChangeType.Add);

        // Assert - a silently-skipped type would leave the change history blind to it
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(change.AttributeChanges[0].ValueChanges, Has.Count.EqualTo(1),
            $"{type} must be recorded in Metaverse Object change history.");
    }

    [TestCaseSource(nameof(CriteriaSupportedTypes))]
    public void SearchComparisonOperators_ValidOperatorsFor_CriteriaSupportedType_ReturnsOperators(AttributeDataType type)
    {
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(type), Is.Not.Empty,
            $"{type} must offer search/scoping comparison operators.");
    }

    [Test]
    public void SearchComparisonOperators_ValidOperatorsFor_UnsupportedTypes_ReturnNoOperators()
    {
        // Binary, Reference and NotSet are deliberately not criteria-capable; this pins the boundary
        // so a future member landing in the wrong bucket is a conscious choice, not an accident
        var unsupported = Enum.GetValues<AttributeDataType>().Except(CriteriaSupportedTypes);
        foreach (var type in unsupported)
            Assert.That(SearchComparisonOperators.ValidOperatorsFor(type), Is.Empty,
                $"{type} unexpectedly offers criteria operators; if deliberate, move it to CriteriaSupportedTypes.");
    }
}
