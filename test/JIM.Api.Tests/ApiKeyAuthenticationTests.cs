using JIM.Web.Middleware.Api;
using NUnit.Framework;

namespace JIM.Api.Tests;

[TestFixture]
public class ApiKeyAuthenticationTests
{
    #region GenerateApiKey tests

    [Test]
    public void GenerateApiKey_ReturnsKeyWithCorrectPrefix()
    {
        var (fullKey, keyPrefix) = ApiKeyAuthenticationHandler.GenerateApiKey();

        Assert.That(fullKey, Does.StartWith("jim_ak_"));
    }

    [Test]
    public void GenerateApiKey_ReturnsPrefixMatchingKeyStart()
    {
        var (fullKey, keyPrefix) = ApiKeyAuthenticationHandler.GenerateApiKey();

        Assert.That(fullKey, Does.StartWith(keyPrefix));
    }

    [Test]
    public void GenerateApiKey_ReturnsPrefixOfLength12()
    {
        var (_, keyPrefix) = ApiKeyAuthenticationHandler.GenerateApiKey();

        // jim_ak_ (7) + 5 chars = 12
        Assert.That(keyPrefix.Length, Is.EqualTo(12));
    }

    [Test]
    public void GenerateApiKey_ReturnsUniqueKeys()
    {
        var (key1, _) = ApiKeyAuthenticationHandler.GenerateApiKey();
        var (key2, _) = ApiKeyAuthenticationHandler.GenerateApiKey();

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void GenerateApiKey_ReturnsKeyWithExpectedLength()
    {
        var (fullKey, _) = ApiKeyAuthenticationHandler.GenerateApiKey();

        // jim_ak_ (7) + 64 hex chars = 71
        Assert.That(fullKey.Length, Is.EqualTo(71));
    }

    #endregion

    #region HashApiKey tests

    [Test]
    public void HashApiKey_ReturnsSameHashForSameKey()
    {
        var key = "jim_ak_test123";

        var hash1 = ApiKeyAuthenticationHandler.HashApiKey(key);
        var hash2 = ApiKeyAuthenticationHandler.HashApiKey(key);

        Assert.That(hash1, Is.EqualTo(hash2));
    }

    [Test]
    public void HashApiKey_ReturnsDifferentHashForDifferentKeys()
    {
        var hash1 = ApiKeyAuthenticationHandler.HashApiKey("jim_ak_test123");
        var hash2 = ApiKeyAuthenticationHandler.HashApiKey("jim_ak_test456");

        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }

    [Test]
    public void HashApiKey_ReturnsLowercaseHex()
    {
        var hash = ApiKeyAuthenticationHandler.HashApiKey("jim_ak_test123");

        Assert.That(hash, Is.EqualTo(hash.ToLowerInvariant()));
    }

    [Test]
    public void HashApiKey_Returns64CharacterHash()
    {
        var hash = ApiKeyAuthenticationHandler.HashApiKey("jim_ak_test123");

        // SHA256 produces 32 bytes = 64 hex characters
        Assert.That(hash.Length, Is.EqualTo(64));
    }

    [Test]
    public void HashApiKey_ReturnsOnlyHexCharacters()
    {
        var hash = ApiKeyAuthenticationHandler.HashApiKey("jim_ak_test123");

        Assert.That(hash, Does.Match("^[0-9a-f]+$"));
    }

    #endregion

    #region Integration tests

    [Test]
    public void GeneratedKey_CanBeHashed()
    {
        var (fullKey, _) = ApiKeyAuthenticationHandler.GenerateApiKey();

        var hash = ApiKeyAuthenticationHandler.HashApiKey(fullKey);

        Assert.That(hash, Is.Not.Null);
        Assert.That(hash.Length, Is.EqualTo(64));
    }

    [Test]
    public void GeneratedKey_ProducesDifferentHashesForDifferentKeys()
    {
        var (key1, _) = ApiKeyAuthenticationHandler.GenerateApiKey();
        var (key2, _) = ApiKeyAuthenticationHandler.GenerateApiKey();

        var hash1 = ApiKeyAuthenticationHandler.HashApiKey(key1);
        var hash2 = ApiKeyAuthenticationHandler.HashApiKey(key2);

        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }

    #endregion
}
