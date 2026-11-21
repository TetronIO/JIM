// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.Http;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How <see cref="OAuthClientCredentialsAuthentication"/> authenticates the client to the token endpoint when given a
/// credential other than a client secret, and how it describes a refused token request. The client secret path is
/// covered by <see cref="ScimAuthenticationTests"/>.
/// </summary>
[TestFixture]
public class OAuthClientCredentialTests
{
    private const string TokenEndpoint = "https://login.example.com/tenant/oauth2/v2.0/token";
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ApplyAsync_CustomCredential_SendsItsParametersInsteadOfAClientSecretAsync()
    {
        var handler = TokenHandler();
        using var tokenClient = new HttpClient(handler);
        var credential = new RecordingCredential();
        var strategy = new OAuthClientCredentialsAuthentication(tokenClient, new Uri(TokenEndpoint), "client-id", credential, "https://graph.example.com/.default", () => Start);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.example.com/v1.0/users");

        await strategy.ApplyAsync(request, CancellationToken.None);

        var body = handler.Requests.Single().Body;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("client_assertion=signed-assertion"));
            Assert.That(body, Does.Contain("client_id=client-id"));
            Assert.That(body, Does.Contain("grant_type=client_credentials"));
            Assert.That(body, Does.Not.Contain("client_secret"));
            Assert.That(credential.Calls, Is.EqualTo(new[] { (new Uri(TokenEndpoint), Start) }));
        }
    }

    [Test]
    public async Task ApplyAsync_CustomCredential_IsAskedAgainForEachTokenRequestAsync()
    {
        // An assertion is single-use and short-lived, so a refreshed token needs a freshly built one.
        var handler = TokenHandler();
        using var tokenClient = new HttpClient(handler);
        var credential = new RecordingCredential();
        var strategy = new OAuthClientCredentialsAuthentication(tokenClient, new Uri(TokenEndpoint), "client-id", credential, scope: null, () => Start);
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://graph.example.com/v1.0/users");
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://graph.example.com/v1.0/users");

        await strategy.ApplyAsync(first, CancellationToken.None);
        strategy.InvalidateCachedCredentials();
        await strategy.ApplyAsync(second, CancellationToken.None);

        Assert.That(credential.Calls, Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyAsync_RefusedWithDescriber_ThrowsTheDescribersMessage()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_client","error_codes":[7000215]}""", Encoding.UTF8, "application/json")
        });
        using var tokenClient = new HttpClient(handler);
        string? seenBody = null;
        var strategy = new OAuthClientCredentialsAuthentication(tokenClient, new Uri(TokenEndpoint), "client-id", new RecordingCredential(), scope: null, () => Start,
            describeFailure: (status, body) =>
            {
                seenBody = body;
                return $"described {(int)status}";
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.example.com/v1.0/users");

        var exception = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await strategy.ApplyAsync(request, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Is.EqualTo("described 401"));
            Assert.That(seenBody, Does.Contain("7000215"));
        }
    }

    [Test]
    public void ApplyAsync_RefusedWithoutDescriber_ThrowsTheGenericMessage()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var tokenClient = new HttpClient(handler);
        var strategy = new OAuthClientCredentialsAuthentication(tokenClient, new Uri(TokenEndpoint), "client-id", new RecordingCredential(), scope: null, () => Start);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.example.com/v1.0/users");

        var exception = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await strategy.ApplyAsync(request, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("HTTP 400"));
    }

    [Test]
    public void Constructor_CleartextEndpointWithCustomCredential_IsRefused()
    {
        using var tokenClient = new HttpClient(TokenHandler());

        Assert.Throws<ArgumentException>(() => _ = new OAuthClientCredentialsAuthentication(
            tokenClient, new Uri("http://login.example.com/token"), "client-id", new RecordingCredential()));
    }

    private static StubHttpMessageHandler TokenHandler() => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"access_token":"token-value","expires_in":3600}""", Encoding.UTF8, "application/json")
    });

    private sealed class RecordingCredential : IOAuthClientCredential
    {
        public List<(Uri TokenEndpoint, DateTimeOffset UtcNow)> Calls { get; } = [];

        public Task<IEnumerable<KeyValuePair<string, string>>> GetTokenRequestParametersAsync(Uri tokenEndpoint, DateTimeOffset utcNow, CancellationToken cancellationToken)
        {
            Calls.Add((tokenEndpoint, utcNow));
            return Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(
            [
                new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                new("client_assertion", "signed-assertion")
            ]);
        }
    }
}
