// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Models.Core;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The server's PATCH applier must be the exact inverse of the shipped client's <see cref="ScimPatchBuilder"/>: what
/// JIM's own client sends to a JIM Service Provider has to land as the client meant it. The oracle is the shipped
/// client's <see cref="ScimResourceWriter.ApplyChanges"/>, which lays the same JIM changes onto a resource directly and
/// has carried the PUT fallback in production export since it shipped. So for every writable attribute and every kind
/// of change, <c>Apply(r, Build(c)) == ApplyChanges(r, c)</c>.
/// <para>
/// The cases are generated from the core schemas, the Enterprise User extension and a provider extension carrying the
/// multi-valued simple attributes the core schemas lack, so an attribute shape added later is covered without a new
/// test. Each change runs against a resource already holding a value for every attribute and against an empty one,
/// which is where the builder's filtered <c>replace</c> on an entry that does not exist yet comes from.
/// </para>
/// </summary>
public class ScimPatchApplierDifferentialTests
{
    private const string CustomUrn = "urn:example:params:scim:schemas:extension:custom:2.0:User";

    private static ScimSchema CustomSchema() => new()
    {
        Id = CustomUrn,
        Name = "Custom",
        Attributes =
        [
            new ScimSchemaAttribute { Name = "badges", Type = ScimAttributeTypes.String, MultiValued = true },
            new ScimSchemaAttribute { Name = "scores", Type = ScimAttributeTypes.Integer, MultiValued = true },
            new ScimSchemaAttribute { Name = "weights", Type = ScimAttributeTypes.Decimal, MultiValued = true },
            new ScimSchemaAttribute { Name = "reviewDates", Type = ScimAttributeTypes.DateTime, MultiValued = true }
        ]
    };

    private static List<ScimFlattenedAttribute> UserAttributes()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.User);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.EnterpriseUser(), ScimAttributeMapper.DeriveNamePrefix(ScimCoreSchemas.EnterpriseUser())));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(CustomSchema(), "custom"));
        return attributes;
    }

    private static List<ScimFlattenedAttribute> GroupAttributes()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.Group);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));
        return attributes;
    }

    private static ScimResourceSchema UserSchema() =>
        ScimResourceSchema.For(ScimCoreSchemas.User(), [ScimCoreSchemas.EnterpriseUser(), CustomSchema()]);

    private static ScimResourceSchema GroupSchema() => ScimResourceSchema.For(ScimCoreSchemas.Group(), []);

    public enum ChangeKind
    {
        Replace,
        Add,
        RemoveOne,
        Clear
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (var (baseUrn, attributes) in new[] { (ScimUrns.User, UserAttributes()), (ScimUrns.Group, GroupAttributes()) })
        {
            foreach (var attribute in attributes.Where(a => a.Writability != AttributeWritability.ReadOnly && !a.IsWriteOnly))
            {
                foreach (var kind in Enum.GetValues<ChangeKind>())
                {
                    foreach (var startFull in new[] { true, false })
                    {
                        yield return new TestCaseData(baseUrn, attribute.Name, kind, startFull)
                            .SetName($"ApplyEqualsApplyChanges_{baseUrn.Split(':').Last()}_{attribute.Name}_{kind}_{(startFull ? "Populated" : "Empty")}");
                    }
                }
            }
        }
    }

    [TestCaseSource(nameof(Cases))]
    public void Apply_BuilderOperations_LandExactlyAsTheClientWriterLaysTheSameChanges(string baseUrn, string attributeName, ChangeKind kind, bool startFull)
    {
        var attributes = baseUrn == ScimUrns.User ? UserAttributes() : GroupAttributes();
        var schema = baseUrn == ScimUrns.User ? UserSchema() : GroupSchema();
        var attribute = attributes.Single(a => a.Name == attributeName);
        var start = startFull ? Populated(attributes, baseUrn) : new JsonObject { ["schemas"] = new JsonArray(JsonValue.Create(baseUrn)) };
        var changes = new List<ScimAttributeChange> { Change(attribute, kind) };

        var built = ScimPatchBuilder.Build(changes, attributes);
        Assert.That(built.UnknownAttributes, Is.Empty, "the builder must accept every writable attribute");

        var expected = ScimResourceWriter.ApplyChanges((JsonObject)start.DeepClone(), changes, attributes).Resource;
        var applied = ScimPatchApplier.Apply((JsonObject)start.DeepClone(), built.Operations, schema);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied.Error, Is.Null, () => $"{attributeName} {kind}: {applied.Error?.ScimType} {applied.Error?.Detail}");
            Assert.That(Canonical(applied.Resource), Is.EqualTo(Canonical(expected)),
                () => $"{attributeName} {kind}, operations {Describe(built.Operations)}");
        }
    }

    [Test]
    public void Cases_CoverEveryShapeTheBuilderEmits()
    {
        // Guards the generator: an empty or narrowed source would pass every case vacuously.
        var names = Cases().Select(c => (string)c.Arguments[1]!).Distinct().ToList();

        Assert.That(names, Is.SupersetOf(new[]
        {
            "userName", "active", "name.givenName", "emails.work", "emails.primary", "addresses.work.streetAddress",
            "roles.value", "x509Certificates.value", "enterpriseUser.manager", "custom.badges", "custom.scores",
            "custom.weights", "custom.reviewDates", "members"
        }));
    }

    /// <summary>
    /// A resource holding a value for every writable attribute, as the client would create it.
    /// </summary>
    private static JsonObject Populated(List<ScimFlattenedAttribute> attributes, string baseUrn)
    {
        var writes = attributes
            .Where(a => a.Writability != AttributeWritability.ReadOnly && !a.IsWriteOnly)
            .Select(a => new ScimAttributeWrite(a.Name, Values(a, 1)))
            .ToList();
        return ScimResourceWriter.BuildResource(writes, attributes, baseUrn).Resource;
    }

    /// <summary>
    /// One change of the given kind. Replace and add use values no populated resource holds, so an add is never a
    /// duplicate (where the applier, following RFC 7644 section 3.5.2.1, leaves an existing value alone and the client
    /// writer appends it again); a removal names a value the populated resource does hold.
    /// </summary>
    private static ScimAttributeChange Change(ScimFlattenedAttribute attribute, ChangeKind kind) => kind switch
    {
        ChangeKind.Replace => new ScimAttributeChange(attribute.Name, ScimPatchOperations.Replace, Sample(attribute, 7)),
        ChangeKind.Add => new ScimAttributeChange(attribute.Name, ScimPatchOperations.Add, Sample(attribute, 8)),
        ChangeKind.RemoveOne => new ScimAttributeChange(attribute.Name, ScimPatchOperations.Remove, Sample(attribute, 1)),
        _ => new ScimAttributeChange(attribute.Name, ScimPatchOperations.Remove, null)
    };

    private static List<object?> Values(ScimFlattenedAttribute attribute, int first)
    {
        var count = attribute.AttributePlurality == AttributePlurality.MultiValued ? 2 : 1;
        return Enumerable.Range(first, count).Select(i => Sample(attribute, i)).ToList();
    }

    private static object? Sample(ScimFlattenedAttribute attribute, int i) => attribute.Type switch
    {
        // A Boolean has only two values; the populated resource holds true, so a replace or add says false.
        AttributeDataType.Boolean => i == 1,
        AttributeDataType.Number => i,
        AttributeDataType.LongNumber => (long)i,
        AttributeDataType.Decimal => i + 0.5m,
        AttributeDataType.DateTime => new DateTime(2026, 1, i, 9, 0, 0, DateTimeKind.Utc),
        AttributeDataType.Binary => new byte[] { (byte)i, 0xAB },
        _ => $"{attribute.Name}-{i}"
    };

    /// <summary>
    /// The resource as comparable text: members in name order, the <c>schemas</c> member left out (the client writer's
    /// <c>ApplyChanges</c> never touches it, and the Service Provider sets it when it writes the resource), and absent,
    /// null and emptied members treated alike, since a resource holding <c>"name": {}</c> holds no name.
    /// </summary>
    private static string Canonical(JsonNode? node)
    {
        var normalised = Normalise(node);
        if (normalised is JsonObject root)
            root.Remove("schemas");
        return normalised?.ToJsonString() ?? "{}";
    }

    private static JsonNode? Normalise(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                var ordered = new JsonObject();
                foreach (var (name, member) in value.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    var normalised = Normalise(member);
                    if (normalised != null)
                        ordered[name] = normalised;
                }
                return ordered.Count == 0 ? null : ordered;

            case JsonArray entries:
                var kept = entries.Select(Normalise).Where(entry => entry != null).ToArray();
                return kept.Length == 0 ? null : new JsonArray(kept);

            case null:
                return null;

            default:
                return node.DeepClone();
        }
    }

    private static string Describe(IEnumerable<ScimPatchOperation> operations) =>
        string.Join("; ", operations.Select(o => $"{o.Op} {o.Path} {o.Value?.ToJsonString()}"));
}
