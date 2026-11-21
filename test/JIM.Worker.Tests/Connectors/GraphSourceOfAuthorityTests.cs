// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphSourceOfAuthorityTests
{
    [TestCase("""{"onPremisesSyncEnabled":null,"userType":"Member"}""", true, false, GraphSourceOfAuthority.Cloud)]
    [TestCase("""{"onPremisesSyncEnabled":false,"userType":"Member"}""", true, false, GraphSourceOfAuthority.Cloud)]
    [TestCase("""{"onPremisesSyncEnabled":true,"userType":"Member"}""", true, false, GraphSourceOfAuthority.OnPremisesDirectory)]
    [TestCase("""{"onPremisesSyncEnabled":true,"userType":"Member"}""", true, true, GraphSourceOfAuthority.Cloud)]
    [TestCase("""{"onPremisesSyncEnabled":null,"userType":"Guest"}""", true, false, GraphSourceOfAuthority.ExternalEntraDirectory)]
    [TestCase("""{"onPremisesSyncEnabled":null,"userType":"Member","creationType":"Invitation"}""", true, false, GraphSourceOfAuthority.ExternalEntraDirectory)]
    [TestCase("""{"onPremisesSyncEnabled":null,"userType":"Member","externalUserState":"Accepted"}""", true, false, GraphSourceOfAuthority.ExternalEntraDirectory)]
    [TestCase("""{"onPremisesSyncEnabled":true,"userType":"Guest"}""", true, false, GraphSourceOfAuthority.ExternalEntraDirectory)]
    [TestCase("""{"onPremisesSyncEnabled":true}""", false, false, GraphSourceOfAuthority.OnPremisesDirectory)]
    [TestCase("""{"onPremisesSyncEnabled":true}""", false, true, GraphSourceOfAuthority.Cloud)]
    [TestCase("""{"onPremisesSyncEnabled":null}""", false, false, GraphSourceOfAuthority.Cloud)]
    [TestCase("""{"userType":"Guest"}""", false, false, GraphSourceOfAuthority.Cloud)]
    public void Derive_AppliesTheRulesInOrder(string json, bool isUser, bool isCloudManaged, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.That(GraphSourceOfAuthority.Derive(document.RootElement, isUser, isCloudManaged), Is.EqualTo(expected));
    }
}
