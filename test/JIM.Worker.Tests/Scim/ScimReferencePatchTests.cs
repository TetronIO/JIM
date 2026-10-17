// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Scim.Service;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The direct path for membership changes (2b): a PATCH that only adds or removes values of a multi-valued reference
/// attribute becomes row inserts and deletes, reading only the references the request names, so one member added to a
/// Group of 100,000 reads none of them. It runs the real PATCH applier over just those references, and the
/// differential test holds it to the result of loading the whole Group and applying the request to it.
/// </summary>
public class ScimReferencePatchTests
{
    private static readonly ScimServiceResourceType GroupType = CoreType("Group");
    private static readonly ScimServiceResourceType UserType = CoreType("User");

    [Test]
    public void Compose_AddsEachReferenceAttributeAsEntries()
    {
        var member = Guid.NewGuid();
        var manager = Guid.NewGuid();

        var group = ScimReferenceEntries.Compose(new JsonObject { ["displayName"] = "Staff" }, GroupType,
            Held(("members", member, "User")));
        var user = ScimReferenceEntries.Compose(new JsonObject { ["userName"] = "ada" }, UserType,
            Held(($"{ScimUrns.EnterpriseUser}:manager", manager, "User")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group["members"]!.ToJsonString(), Is.EqualTo($$"""[{"value":"{{member}}","type":"User"}]"""));
            Assert.That(user[ScimUrns.EnterpriseUser]!["manager"]!.ToJsonString(), Is.EqualTo($$"""{"value":"{{manager}}","type":"User"}"""),
                "a single-valued reference is one object, inside its extension");
        }
    }

    [Test]
    public void Split_TakesReferenceAttributesOutOfThePayload()
    {
        var member = Guid.NewGuid();
        var manager = Guid.NewGuid();
        var resource = new JsonObject
        {
            ["userName"] = "ada",
            [ScimUrns.EnterpriseUser] = new JsonObject { ["department"] = "R&D", ["manager"] = new JsonObject { ["value"] = manager.ToString().ToUpperInvariant() } }
        };
        var group = new JsonObject { ["displayName"] = "Staff", ["members"] = new JsonArray(new JsonObject { ["value"] = member.ToString() }) };

        var splitUser = ScimReferenceEntries.Split(resource, UserType);
        var splitGroup = ScimReferenceEntries.Split(group, GroupType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(splitUser.Error, Is.Null);
            Assert.That(splitUser.Payload.ToJsonString(), Is.EqualTo($$$"""{"userName":"ada","{{{ScimUrns.EnterpriseUser}}}":{"department":"R\u0026D"}}"""));
            Assert.That(splitUser.References[$"{ScimUrns.EnterpriseUser}:manager"], Is.EqualTo(new[] { manager }));
            Assert.That(splitGroup.Payload.ToJsonString(), Is.EqualTo("""{"displayName":"Staff"}"""));
            Assert.That(splitGroup.References["members"], Is.EqualTo(new[] { member }));
        }
    }

    [Test]
    public void Split_EmptyExtensionLeftByAManager_IsRemoved()
    {
        var resource = new JsonObject
        {
            ["userName"] = "ada",
            [ScimUrns.EnterpriseUser] = new JsonObject { ["manager"] = new JsonObject { ["value"] = Guid.NewGuid().ToString() } }
        };

        Assert.That(ScimReferenceEntries.Split(resource, UserType).Payload.ToJsonString(), Is.EqualTo("""{"userName":"ada"}"""));
    }

    [Test]
    public void Split_EveryReferenceAttribute_IsPresentEvenWhenEmpty()
    {
        var split = ScimReferenceEntries.Split(new JsonObject { ["displayName"] = "Staff" }, GroupType);

        Assert.That(split.References["members"], Is.Empty);
    }

    [TestCase("not-a-guid")]
    [TestCase("")]
    public void Split_ValueThatCannotBeAResource_IsRefused(string value)
    {
        var group = new JsonObject { ["displayName"] = "Staff", ["members"] = new JsonArray(new JsonObject { ["value"] = value }) };

        var split = ScimReferenceEntries.Split(group, GroupType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(split.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(split.Error?.Detail, Does.Contain("members"));
            if (value.Length > 0)
                Assert.That(split.Error?.Detail, Does.Not.Contain(value), "the detail never repeats a value");
        }
    }

    [Test]
    public void Split_DuplicateValues_AreOneReference()
    {
        var member = Guid.NewGuid();
        var group = new JsonObject
        {
            ["displayName"] = "Staff",
            ["members"] = new JsonArray(new JsonObject { ["value"] = member.ToString() }, new JsonObject { ["value"] = member.ToString().ToUpperInvariant() })
        };

        Assert.That(ScimReferenceEntries.Split(group, GroupType).References["members"], Is.EqualTo(new[] { member }));
    }

    [TestCase("""[{"op":"add","path":"members","value":[{"value":"%A"}]}]""")]
    [TestCase("""[{"op":"Add","path":"members","value":[{"value":"%A","display":"Ada"}]}]""")]
    [TestCase("""[{"op":"remove","path":"members[value eq \"%A\"]"}]""")]
    [TestCase("""[{"op":"remove","path":"members","value":[{"value":"%A"}]}]""")]
    [TestCase("""[{"op":"replace","path":"members","value":[{"value":"%A"}]}]""")]
    [TestCase("""[{"op":"remove","path":"members"}]""")]
    [TestCase("""[{"op":"add","path":"members","value":{"value":"%A"}}]""")]
    public void TryPlan_MembershipOnly_IsPlanned(string operations)
    {
        Assert.That(ScimReferencePatch.TryPlan(Operations(operations), GroupType), Is.Not.Null);
    }

    [TestCase("""[{"op":"replace","path":"displayName","value":"Staff"}]""", TestName = "TryPlan_AnotherAttribute_FallsBack")]
    [TestCase("""[{"op":"add","path":"members","value":[{"value":"%A"}]},{"op":"replace","path":"displayName","value":"x"}]""", TestName = "TryPlan_MembersAndAnotherAttribute_FallsBack")]
    [TestCase("""[{"op":"add","value":{"members":[{"value":"%A"}]}}]""", TestName = "TryPlan_NoPath_FallsBack")]
    [TestCase("""[{"op":"remove","path":"members[type eq \"User\"]"}]""", TestName = "TryPlan_FilterOtherThanValueEquality_FallsBack")]
    [TestCase("""[{"op":"remove","path":"members[value co \"%A\"]"}]""", TestName = "TryPlan_ValueFilterOtherThanEq_FallsBack")]
    [TestCase("""[{"op":"replace","path":"members[value eq \"%A\"].type","value":"Group"}]""", TestName = "TryPlan_SubAttributeOfFilteredEntry_FallsBack")]
    [TestCase("""[{"op":"remove","path":"members","value":[{"type":"User"}]}]""", TestName = "TryPlan_ListedEntryWithoutValue_FallsBack")]
    [TestCase("""[{"op":"move","path":"members","value":[{"value":"%A"}]}]""", TestName = "TryPlan_UnknownOp_FallsBack")]
    [TestCase("""[{"op":"add","path":"members[","value":[{"value":"%A"}]}]""", TestName = "TryPlan_InvalidPath_FallsBack")]
    [TestCase("""[]""", TestName = "TryPlan_NoOperations_FallsBack")]
    public void TryPlan_OutsideTheDirectPath_FallsBackToTheApplier(string operations)
    {
        Assert.That(ScimReferencePatch.TryPlan(Operations(operations), GroupType), Is.Null);
    }

    [Test]
    public void TryPlan_SingleValuedReference_FallsBack()
    {
        var operations = Operations($$$"""[{"op":"replace","path":"{{{ScimUrns.EnterpriseUser}}}:manager","value":{"value":"%A"}}]""");

        Assert.That(ScimReferencePatch.TryPlan(operations, UserType), Is.Null);
    }

    [Test]
    public void TryPlan_NamesOnlyTheReferencesTheRequestMentions()
    {
        var plan = ScimReferencePatch.TryPlan(Operations("""
            [{"op":"add","path":"members","value":[{"value":"%A"},{"value":"%B"}]},
             {"op":"remove","path":"members[value eq \"%C\"]"},
             {"op":"remove","path":"members[value eq \"not-a-guid\"]"}]
            """), GroupType)!;

        Assert.That(plan.MentionedIds["members"], Is.EquivalentTo(new[] { A, B, C }));
    }

    [Test]
    public void Resolve_AddsAndRemoves_AgainstWhatIsHeld()
    {
        var plan = ScimReferencePatch.TryPlan(Operations("""
            [{"op":"add","path":"members","value":[{"value":"%A"},{"value":"%B"}]},
             {"op":"remove","path":"members[value eq \"%C\"]"}]
            """), GroupType)!;

        var outcome = plan.Resolve(Held(("members", B, "User"), ("members", C, "User")));

        var change = outcome.Changes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Error, Is.Null);
            Assert.That(change.ReplaceAll, Is.False);
            Assert.That(change.Add, Is.EquivalentTo(new[] { A }), "B is already a member");
            Assert.That(change.Remove, Is.EquivalentTo(new[] { C }));
        }
    }

    [Test]
    public void Resolve_RemoveAll_ReplacesWithWhatFollows()
    {
        var plan = ScimReferencePatch.TryPlan(Operations("""
            [{"op":"remove","path":"members"},{"op":"add","path":"members","value":[{"value":"%A"}]}]
            """), GroupType)!;

        var change = plan.Resolve(Held()).Changes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ReplaceAll, Is.True);
            Assert.That(change.Add, Is.EquivalentTo(new[] { A }));
        }
    }

    [Test]
    public void Resolve_NothingChanges_HasNoChanges()
    {
        var plan = ScimReferencePatch.TryPlan(Operations("""
            [{"op":"add","path":"members","value":[{"value":"%A"}]},{"op":"remove","path":"members[value eq \"%B\"]"}]
            """), GroupType)!;

        Assert.That(plan.Resolve(Held(("members", A, "User"))).Changes, Is.Empty);
    }

    [Test]
    public void Resolve_AddingAValueThatCannotBeAResource_IsRefused()
    {
        var plan = ScimReferencePatch.TryPlan(Operations("""[{"op":"add","path":"members","value":[{"value":"nope"}]}]"""), GroupType)!;

        Assert.That(plan.Resolve(Held()).Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
    }

    [Test]
    public void Apply_RemoveListingAReadOnlyDisplay_StillRemovesTheMember()
    {
        // Identity providers echo display back in a remove; the held entry never carries it (it is read-only), so a
        // reference is identified by its value alone. Matching every member would leave the member in the Group.
        var group = ScimReferenceEntries.Compose(new JsonObject { ["displayName"] = "Staff" }, GroupType, Held(("members", A, "User")));

        var result = ScimPatchApplier.Apply(group, Operations("""[{"op":"remove","path":"members","value":[{"value":"%A","display":"Ada"}]}]"""), GroupType.Schema);

        Assert.That(result.Resource!.ContainsKey("members"), Is.False);
    }

    /// <summary>
    /// The direct path and the whole-resource path agree on every request in the direct path's reach: same members
    /// afterwards, and the same requests refused.
    /// </summary>
    [Test]
    public void DirectPath_AgreesWithTheApplierOnTheWholeGroup([Range(0, 499)] int seed)
    {
        var random = new Random(seed);
        var pool = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var held = pool.Where(_ => random.Next(2) == 0).ToList();
        var operations = RandomOperations(random, pool);

        var plan = ScimReferencePatch.TryPlan(operations, GroupType);
        Assert.That(plan, Is.Not.Null, "every generated request is in the direct path's reach");

        var whole = ScimPatchApplier.Apply(
            ScimReferenceEntries.Compose(new JsonObject { ["displayName"] = "Staff" }, GroupType, Held(held.Select(id => ("members", id, "User")).ToArray())),
            operations, GroupType.Schema);
        var wholeSplit = whole.Resource == null ? null : ScimReferenceEntries.Split(whole.Resource, GroupType);
        var wholeError = whole.Error ?? wholeSplit?.Error;

        var mentioned = plan!.MentionedIds.GetValueOrDefault("members") ?? [];
        var outcome = plan.Resolve(Held(held.Where(mentioned.Contains).Select(id => ("members", id, "User")).ToArray()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Error?.ScimType, Is.EqualTo(wholeError?.ScimType), Describe(operations, held));
            if (wholeError == null)
                Assert.That(ApplyChanges(held, outcome.Changes), Is.EquivalentTo(wholeSplit!.References["members"]), Describe(operations, held));
        }
    }

    private static List<Guid> ApplyChanges(List<Guid> held, IReadOnlyList<ScimReferenceChange> changes)
    {
        var result = new HashSet<Guid>(held);
        foreach (var change in changes)
        {
            if (change.ReplaceAll)
                result.Clear();
            result.ExceptWith(change.Remove);
            result.UnionWith(change.Add);
        }

        return result.ToList();
    }

    private static List<ScimPatchOperation> RandomOperations(Random random, List<Guid> pool)
    {
        string Id() => random.Next(4) == 0 ? pool[random.Next(pool.Count)].ToString().ToUpperInvariant() : pool[random.Next(pool.Count)].ToString();
        string Op(string op) => random.Next(5) == 0 ? char.ToUpperInvariant(op[0]) + op[1..] : op;
        JsonNode Entry()
        {
            var entry = new JsonObject { ["value"] = random.Next(12) == 0 ? "not-a-guid" : Id() };
            if (random.Next(3) == 0)
                entry["type"] = "User";
            if (random.Next(3) == 0)
                entry["display"] = "Someone";
            return entry;
        }
        JsonNode Entries()
        {
            if (random.Next(5) == 0)
                return Entry();
            var array = new JsonArray();
            for (var i = random.Next(4); i > 0; i--)
                array.Add(Entry());
            return array;
        }

        var operations = new List<ScimPatchOperation>();
        for (var i = 1 + random.Next(4); i > 0; i--)
        {
            operations.Add(random.Next(6) switch
            {
                0 or 1 => new ScimPatchOperation { Op = Op("add"), Path = "members", Value = Entries() },
                2 => new ScimPatchOperation { Op = Op("remove"), Path = $"members[value eq \"{Id()}\"]" },
                3 => new ScimPatchOperation { Op = Op("remove"), Path = "members", Value = Entries() },
                4 => new ScimPatchOperation { Op = Op("replace"), Path = "members", Value = random.Next(4) == 0 ? null : Entries() },
                _ => new ScimPatchOperation { Op = Op("remove"), Path = "members" }
            });
        }

        return operations;
    }

    private static string Describe(List<ScimPatchOperation> operations, List<Guid> held) =>
        $"held [{string.Join(", ", held)}]; operations {string.Join(" ", operations.Select(o => $"{o.Op} {o.Path} {o.Value?.ToJsonString()}"))}";

    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static List<ScimPatchOperation> Operations(string json)
    {
        var text = json.Replace("%A", A.ToString()).Replace("%B", B.ToString()).Replace("%C", C.ToString());
        return JsonNode.Parse(text)!.AsArray().Select(node => new ScimPatchOperation
        {
            Op = node!["op"]!.GetValue<string>(),
            Path = node["path"]?.GetValue<string>(),
            Value = node["value"]?.DeepClone()
        }).ToList();
    }

    private static Dictionary<string, IReadOnlyList<ScimHeldReference>> Held(params (string Attribute, Guid Id, string Type)[] references) =>
        references.GroupBy(r => r.Attribute)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScimHeldReference>)g.Select(r => new ScimHeldReference(r.Id, r.Type)).ToList());

    private static ScimServiceResourceType CoreType(string name)
    {
        var (schemas, types) = ScimCoreDefinitions.Rows();
        for (var i = 0; i < types.Count; i++)
            types[i].Id = i + 1;
        return ScimServiceDefinitions.Compile(types, schemas).FindByName(name)!;
    }
}
