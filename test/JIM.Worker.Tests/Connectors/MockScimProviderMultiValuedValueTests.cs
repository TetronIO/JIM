// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JIM.TestScimServiceProvider;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The test service provider refuses a scalar add or replace on a multi-valued simple attribute, as a provider that
/// validates values against its schema does (RFC 7644 section 3.5.2). Lenient, it would let Scenario 15 and the export
/// tests pass against a request a strict provider refuses (#2215).
/// </summary>
[TestFixture]
public class MockScimProviderMultiValuedValueTests
{
    private static readonly string BadgesPath = $"{MockScimProvider.CustomUserExtensionUrn}:badges";

    [TestCase("add")]
    [TestCase("replace")]
    public async Task Respond_ScalarValueForAMultiValuedSimpleAttribute_IsRefusedAsync(string op)
    {
        var provider = NewProvider();

        using var response = provider.Respond(Patch(op, "\"gold\""));
        var body = await response.Content.ReadAsStringAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(body, Does.Contain("invalidValue"));
            Assert.That(Badges(provider), Is.Null, "a refused request changes nothing");
        }
    }

    [TestCase("add")]
    [TestCase("replace")]
    public void Respond_ArrayValueForAMultiValuedSimpleAttribute_IsApplied(string op)
    {
        var provider = NewProvider();

        using var response = provider.Respond(Patch(op, "[\"gold\"]"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Badges(provider)?.ToJsonString(), Is.EqualTo("""["gold"]"""));
        }
    }

    private static MockScimProvider NewProvider()
    {
        var provider = new MockScimProvider();
        provider.Options.PublishesCustomUserExtension = true;
        provider.AddUser("u1", "alice");
        return provider;
    }

    private static JsonArray? Badges(MockScimProvider provider) =>
        (provider.Resources.Single().Attributes.GetValueOrDefault(MockScimProvider.CustomUserExtensionUrn) as JsonObject)?["badges"] as JsonArray;

    private static HttpRequestMessage Patch(string op, string value) =>
        new(HttpMethod.Patch, new Uri("https://scim.example.test/scim/v2/Users/u1"))
        {
            Content = new StringContent(
                $$"""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"{{op}}","path":"{{BadgesPath}}","value":{{value}}}]}""",
                Encoding.UTF8, "application/scim+json")
        };
}
