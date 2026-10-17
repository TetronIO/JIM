// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Holds the SCIM Standard Mappings on the built-in Metaverse schema to the attribute names and types JIM.Scim's
/// flattening actually produces (#2095). The Attribute Flow editor matches a counterpart against a Connected System
/// attribute's name exactly, so a counterpart naming an RFC 7643 path (<c>phoneNumbers</c>) rather than the
/// flattened slot (<c>phoneNumbers.mobile</c>) can never produce a suggestion. Lives here rather than in
/// JIM.Models.Tests because that project cannot reference JIM.Scim.
/// </summary>
public class ScimStandardMappingConformanceTests
{
    /// <summary>
    /// Metaverse Attributes whose SCIM counterpart is documented as needing a transform rather than a direct flow, so
    /// their data types are expected to differ. Each entry says so in its mapping's Notes.
    /// </summary>
    private static readonly HashSet<string> DocumentedTransforms = new(StringComparer.Ordinal)
    {
        // SCIM photos carry URLs; the Metaverse Attribute holds the image itself.
        Constants.BuiltInAttributes.Photo
    };

    private static readonly IReadOnlyDictionary<string, List<ScimFlattenedAttribute>> FlattenedByObjectType = new Dictionary<string, List<ScimFlattenedAttribute>>
    {
        [Constants.BuiltInObjectTypes.User] = FlattenUser(),
        [Constants.BuiltInObjectTypes.Group] = ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group())
    };

    private static List<ScimFlattenedAttribute> FlattenUser()
    {
        var enterpriseUser = ScimCoreSchemas.EnterpriseUser();
        return ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User())
            .Concat(ScimAttributeMapper.FlattenSchema(enterpriseUser, ScimAttributeMapper.DeriveNamePrefix(enterpriseUser)))
            .ToList();
    }

    private static IEnumerable<TestCaseData> ScimMappings() =>
        BuiltInMetaverseSchema.Attributes
            .SelectMany(attribute => attribute.StandardMappings
                .Where(mapping => mapping.Standard == AttributeStandard.Scim)
                .Select(mapping => new TestCaseData(attribute, mapping).SetArgDisplayNames(attribute.Name, mapping.CounterpartName)));

    [Test]
    public void BuiltInSchema_HasScimStandardMappings()
    {
        // Guards the guard: if the catalogue's SCIM mappings ever vanished, every case below would vanish with them
        // and the fixture would pass vacuously.
        Assert.That(ScimMappings().Count(), Is.GreaterThan(0));
    }

    [TestCaseSource(nameof(ScimMappings))]
    public void ScimStandardMapping_CounterpartIsAnAttributeTheFlatteningProduces(BuiltInMetaverseAttributeDefinition attribute, StandardMappingDefinition mapping)
    {
        var produced = ProducedFor(attribute, mapping);

        Assert.That(produced, Is.Not.Null,
            $"The SCIM counterpart '{mapping.CounterpartName}' of '{attribute.Name}' is not an attribute JIM.Scim produces " +
            $"for {string.Join(" or ", attribute.ObjectTypeNames)}, so the Attribute Flow editor can never suggest it.");
    }

    [TestCaseSource(nameof(ScimMappings))]
    public void ScimStandardMapping_CounterpartDataTypeMatchesTheMetaverseAttribute(BuiltInMetaverseAttributeDefinition attribute, StandardMappingDefinition mapping)
    {
        var produced = ProducedFor(attribute, mapping);
        Assume.That(produced, Is.Not.Null, "Covered by the name test.");

        if (DocumentedTransforms.Contains(attribute.Name))
        {
            Assert.That(produced!.Type, Is.Not.EqualTo(attribute.Type),
                $"'{attribute.Name}' is listed as a documented transform but its types now agree; remove it from the list.");
            return;
        }

        Assert.That(produced!.Type, Is.EqualTo(attribute.Type),
            $"'{mapping.CounterpartName}' flattens as {produced.Type} but '{attribute.Name}' is {attribute.Type}, so the editor " +
            "refuses the flow as a type mismatch.");
    }

    private static ScimFlattenedAttribute? ProducedFor(BuiltInMetaverseAttributeDefinition attribute, StandardMappingDefinition mapping) =>
        attribute.ObjectTypeNames
            .Where(FlattenedByObjectType.ContainsKey)
            .SelectMany(objectType => FlattenedByObjectType[objectType])
            .FirstOrDefault(flattened => string.Equals(flattened.Name, mapping.CounterpartName, StringComparison.OrdinalIgnoreCase));
}
