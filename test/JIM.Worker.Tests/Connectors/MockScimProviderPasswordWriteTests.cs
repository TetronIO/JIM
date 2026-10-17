// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.TestScimServiceProvider;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The test service provider counts every write that carries a <c>password</c>, in any of the shapes SCIM allows, so
/// Scenario 15 and the export tests can prove JIM sends none (#2091). A detector that missed a shape would make that
/// proof vacuous, so each shape is exercised here.
/// </summary>
[TestFixture]
public class MockScimProviderPasswordWriteTests
{
    private const string UserUrn = "urn:ietf:params:scim:schemas:core:2.0:User";

    private static IEnumerable<TestCaseData> WritesCarryingAPassword()
    {
        yield return Case("create", HttpMethod.Post, "Users",
            """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"new","password":"S3cret!"}""");
        yield return Case("replace", HttpMethod.Put, "Users/u1",
            """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"alice","PASSWORD":"S3cret!"}""");
        yield return Case("patch by path", HttpMethod.Patch, "Users/u1",
            """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","path":"password","value":"S3cret!"}]}""");
        yield return Case("patch by URN path", HttpMethod.Patch, "Users/u1",
            $$"""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","path":"{{UserUrn}}:password","value":"S3cret!"}]}""");
        yield return Case("patch without a path", HttpMethod.Patch, "Users/u1",
            """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","value":{"password":"S3cret!"}}]}""");

        static TestCaseData Case(string name, HttpMethod method, string path, string body) =>
            new TestCaseData(method, path, body).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(WritesCarryingAPassword))]
    public void Respond_WriteCarryingAPassword_IsCounted(HttpMethod method, string path, string body)
    {
        var provider = NewProvider();

        provider.Respond(Request(method, path, body)).Dispose();

        Assert.That(provider.PasswordWritesReceived, Is.EqualTo(1));
    }

    [Test]
    public void Respond_BulkOperationCarryingAPassword_IsCountedOnce()
    {
        var provider = NewProvider();
        provider.Options.SupportsBulk = true;

        provider.Respond(Request(HttpMethod.Post, "Bulk", """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:BulkRequest"],"Operations":[
              {"method":"POST","path":"/Users","bulkId":"b1","data":{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"new","password":"S3cret!"}}]}
            """)).Dispose();

        Assert.That(provider.PasswordWritesReceived, Is.EqualTo(1));
    }

    [Test]
    public void Respond_WritesWithoutAPassword_AreNotCounted()
    {
        var provider = NewProvider();

        provider.Respond(Request(HttpMethod.Post, "Users",
            """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"password","title":"password"}""")).Dispose();
        provider.Respond(Request(HttpMethod.Patch, "Users/u1",
            """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","path":"title","value":"passwordless"}]}""")).Dispose();

        Assert.That(provider.PasswordWritesReceived, Is.Zero, "a value that merely says password is not a password attribute");
    }

    private static MockScimProvider NewProvider()
    {
        var provider = new MockScimProvider();
        provider.AddUser("u1", "alice");
        return provider;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string body) =>
        new(method, new Uri($"https://scim.example.test/scim/v2/{path}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };
}
