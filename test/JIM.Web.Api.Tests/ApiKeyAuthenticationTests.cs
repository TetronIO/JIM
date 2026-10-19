// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Security.Claims;
using JIM.Application.Utilities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Middleware.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class ApiKeyAuthenticationTests
{
    #region GenerateApiKey tests

    [Test]
    public void GenerateApiKey_ReturnsKeyWithCorrectPrefix()
    {
        var (fullKey, keyPrefix) = ApiKeyMaterial.Generate();

        Assert.That(fullKey, Does.StartWith("jim_ak_"));
    }

    [Test]
    public void GenerateApiKey_ReturnsPrefixMatchingKeyStart()
    {
        var (fullKey, keyPrefix) = ApiKeyMaterial.Generate();

        Assert.That(fullKey, Does.StartWith(keyPrefix));
    }

    [Test]
    public void GenerateApiKey_ReturnsPrefixOfLength12()
    {
        var (_, keyPrefix) = ApiKeyMaterial.Generate();

        // jim_ak_ (7) + 5 chars = 12
        Assert.That(keyPrefix.Length, Is.EqualTo(12));
    }

    [Test]
    public void GenerateApiKey_ReturnsUniqueKeys()
    {
        var (key1, _) = ApiKeyMaterial.Generate();
        var (key2, _) = ApiKeyMaterial.Generate();

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void GenerateApiKey_ReturnsKeyWithExpectedLength()
    {
        var (fullKey, _) = ApiKeyMaterial.Generate();

        // jim_ak_ (7) + 64 hex chars = 71
        Assert.That(fullKey.Length, Is.EqualTo(71));
    }

    #endregion

    #region HashApiKey tests

    [Test]
    public void HashApiKey_ReturnsSameHashForSameKey()
    {
        var key = "jim_ak_test123";

        var hash1 = ApiKeyMaterial.Hash(key);
        var hash2 = ApiKeyMaterial.Hash(key);

        Assert.That(hash1, Is.EqualTo(hash2));
    }

    [Test]
    public void HashApiKey_ReturnsDifferentHashForDifferentKeys()
    {
        var hash1 = ApiKeyMaterial.Hash("jim_ak_test123");
        var hash2 = ApiKeyMaterial.Hash("jim_ak_test456");

        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }

    [Test]
    public void HashApiKey_ReturnsLowercaseHex()
    {
        var hash = ApiKeyMaterial.Hash("jim_ak_test123");

        Assert.That(hash, Is.EqualTo(hash.ToLowerInvariant()));
    }

    [Test]
    public void HashApiKey_Returns64CharacterHash()
    {
        var hash = ApiKeyMaterial.Hash("jim_ak_test123");

        // SHA256 produces 32 bytes = 64 hex characters
        Assert.That(hash.Length, Is.EqualTo(64));
    }

    [Test]
    public void HashApiKey_ReturnsOnlyHexCharacters()
    {
        var hash = ApiKeyMaterial.Hash("jim_ak_test123");

        Assert.That(hash, Does.Match("^[0-9a-f]+$"));
    }

    #endregion

    #region Integration tests

    [Test]
    public void GeneratedKey_CanBeHashed()
    {
        var (fullKey, _) = ApiKeyMaterial.Generate();

        var hash = ApiKeyMaterial.Hash(fullKey);

        Assert.That(hash, Is.Not.Null);
        Assert.That(hash.Length, Is.EqualTo(64));
    }

    [Test]
    public void GeneratedKey_ProducesDifferentHashesForDifferentKeys()
    {
        var (key1, _) = ApiKeyMaterial.Generate();
        var (key2, _) = ApiKeyMaterial.Generate();

        var hash1 = ApiKeyMaterial.Hash(key1);
        var hash2 = ApiKeyMaterial.Hash(key2);

        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }

    #endregion

    #region BuildApiKeyClaims tests

    [Test]
    public void BuildApiKeyClaims_InfrastructureKey_IncludesInfrastructureClaim()
    {
        var apiKey = new ApiKey
        {
            Id = System.Guid.NewGuid(),
            Name = "Infrastructure Key",
            KeyPrefix = "jim_ak_abcde",
            IsInfrastructureKey = true
        };

        var claims = ApiKeyAuthenticationHandler.BuildApiKeyClaims(apiKey);

        Assert.That(claims.Any(c => c.Type == Constants.BuiltInClaims.IsInfrastructureKey && c.Value == "true"), Is.True);
    }

    [Test]
    public void BuildApiKeyClaims_OrdinaryKey_OmitsInfrastructureClaim()
    {
        var apiKey = new ApiKey
        {
            Id = System.Guid.NewGuid(),
            Name = "Ordinary Key",
            KeyPrefix = "jim_ak_fghij",
            IsInfrastructureKey = false
        };

        var claims = ApiKeyAuthenticationHandler.BuildApiKeyClaims(apiKey);

        Assert.That(claims.Any(c => c.Type == Constants.BuiltInClaims.IsInfrastructureKey), Is.False);
    }

    [Test]
    public void BuildApiKeyClaims_AlwaysIncludesIdentityAndVirtualUserRole()
    {
        var apiKey = new ApiKey
        {
            Id = System.Guid.NewGuid(),
            Name = "Some Key",
            KeyPrefix = "jim_ak_klmno",
            IsInfrastructureKey = false
        };

        var claims = ApiKeyAuthenticationHandler.BuildApiKeyClaims(apiKey);

        Assert.That(claims.Any(c => c.Type == ClaimTypes.NameIdentifier && c.Value == apiKey.Id.ToString()), Is.True);
        Assert.That(claims.Any(c => c.Type == ClaimTypes.Name && c.Value == apiKey.Name), Is.True);
        Assert.That(claims.Any(c => c.Type == Constants.BuiltInRoles.RoleClaimType && c.Value == Constants.BuiltInRoles.User), Is.True);
    }

    #endregion
}
