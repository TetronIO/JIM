// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Connectors;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Scim.Discovery;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Middleware.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace JIM.Web.Api.Tests.Scim;

/// <summary>
/// How an administrator manages a Connected System's SCIM 2.0 Service Provider endpoint over the REST API (#124,
/// Phase 5a): its keys (create, then revoke, to rotate), what its store holds, and the definitions it serves. A key is
/// reached only through the Connected System it is scoped to, and every endpoint is refused while the feature is off.
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimServiceProviderControllerDatabaseTests
{
    private string _connectionString = null!;
    private ApiKey _administrator = null!;
    private int _systemId;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment(databaseSuffix: "_web_api");
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM administration tests.");
        _connectionString = connectionString!;
        PostgresTestDatabase.CreateDatabaseIfMissing(_connectionString);

        await using (var ctx = NewContext())
            await ctx.Database.MigrateAsync();
        await WithJimAsync(jim => jim.Seeding.ApplyBuiltInConfigurationAsync());
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await using (var ctx = NewContext())
        {
            _administrator = new ApiKey { Id = Guid.NewGuid(), Name = "Administrator", KeyHash = $"hash-{Guid.NewGuid():N}", KeyPrefix = "jim_ak_test" };
            ctx.ApiKeys.Add(_administrator);
            await ctx.SaveChangesAsync();
        }

        await SetFlagAsync(true);
        _systemId = await CreateServiceProviderSystemAsync();
    }

    [Test]
    public async Task GetKeysAsync_ListsOnlyThisConnectedSystemsKeysWithTheirScopeAsync()
    {
        var other = await CreateServiceProviderSystemAsync();
        await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = "Entra key" }));
        await CallAsync(c => c.CreateKeyAsync(other, new ScimKeyCreateRequestDto { Name = "Okta key" }));

        var keys = Value<IEnumerable<ApiKeyDto>>(await CallAsync(c => c.GetKeysAsync(_systemId))).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Select(k => k.Name), Is.EqualTo(new[] { "Entra key" }));
            Assert.That(keys[0].Scope, Is.EqualTo(ApiKeyScope.ScimServiceProvider));
            Assert.That(keys[0].ScopeConnectedSystemId, Is.EqualTo(_systemId));
        }
    }

    [Test]
    public async Task CreateKeyAsync_ReturnsTheSecretOnceScopedToTheSystemForAYearAsync()
    {
        var result = await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = "Entra key", Description = "Provisioning" }));

        var created = Value<ApiKeyCreateResponseDto>(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
            Assert.That(created.Key, Does.StartWith("jim_ak_"));
            Assert.That(created.Key, Does.StartWith(created.KeyPrefix));
            Assert.That(created.Description, Is.EqualTo("Provisioning"));
            Assert.That(created.Scope, Is.EqualTo(ApiKeyScope.ScimServiceProvider));
            Assert.That(created.ScopeConnectedSystemId, Is.EqualTo(_systemId));
            Assert.That(created.Roles, Is.Empty);
            Assert.That(created.ExpiresAt, Is.EqualTo(DateTime.UtcNow.AddDays(365)).Within(TimeSpan.FromMinutes(5)));
        }

        // Later reads never carry the secret.
        var read = Value<ApiKeyDto>(await CallAsync(c => c.GetKeyAsync(_systemId, created.Id)));
        Assert.That(read, Is.Not.InstanceOf<ApiKeyCreateResponseDto>());
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task CreateKeyAsync_NoName_400Async(string name)
    {
        var result = await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = name }));

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task KeyEndpoints_KeyScopedToAnotherConnectedSystem_404Async()
    {
        var other = await CreateServiceProviderSystemAsync();
        var otherKey = Value<ApiKeyCreateResponseDto>(await CallAsync(c => c.CreateKeyAsync(other, new ScimKeyCreateRequestDto { Name = "Okta key" })));

        var get = await CallAsync(c => c.GetKeyAsync(_systemId, otherKey.Id));
        var update = await CallAsync(c => c.UpdateKeyAsync(_systemId, otherKey.Id, new ScimKeyUpdateRequestDto { IsEnabled = false }));
        var delete = await CallAsync(c => c.DeleteKeyAsync(_systemId, otherKey.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(get, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(update, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(delete, Is.InstanceOf<NotFoundObjectResult>());
        }

        // The other system's key is untouched.
        var stillThere = Value<ApiKeyDto>(await CallAsync(c => c.GetKeyAsync(other, otherKey.Id)));
        Assert.That(stillThere.IsEnabled, Is.True);
    }

    [Test]
    public async Task KeyEndpoints_UnscopedKey_404Async()
    {
        var result = await CallAsync(c => c.GetKeyAsync(_systemId, _administrator.Id));

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateKeyAsync_DisableAndClearDescription_LeavesWhatWasOmittedAsync()
    {
        var created = Value<ApiKeyCreateResponseDto>(await CallAsync(c => c.CreateKeyAsync(_systemId,
            new ScimKeyCreateRequestDto { Name = "Entra key", Description = "Provisioning" })));

        var updated = Value<ApiKeyDto>(await CallAsync(c => c.UpdateKeyAsync(_systemId, created.Id,
            new ScimKeyUpdateRequestDto { IsEnabled = false, Description = "" })));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.IsEnabled, Is.False);
            Assert.That(updated.Description, Is.Null);
            Assert.That(updated.Name, Is.EqualTo("Entra key"));
            Assert.That(updated.ExpiresAt, Is.EqualTo(created.ExpiresAt).Within(TimeSpan.FromSeconds(1)));
            Assert.That(updated.Scope, Is.EqualTo(ApiKeyScope.ScimServiceProvider));
        }
    }

    [Test]
    public async Task UpdateKeyAsync_RenameAndNewExpiry_SavedAsync()
    {
        var created = Value<ApiKeyCreateResponseDto>(await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = "Entra key" })));
        var expiry = DateTime.UtcNow.AddDays(30);

        var updated = Value<ApiKeyDto>(await CallAsync(c => c.UpdateKeyAsync(_systemId, created.Id,
            new ScimKeyUpdateRequestDto { Name = "Entra key 2026", ExpiresAt = expiry })));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Name, Is.EqualTo("Entra key 2026"));
            Assert.That(updated.ExpiresAt, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
            Assert.That(updated.IsEnabled, Is.True);
        }
    }

    [Test]
    public async Task UpdateKeyAsync_BlankName_400Async()
    {
        var created = Value<ApiKeyCreateResponseDto>(await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = "Entra key" })));

        var result = await CallAsync(c => c.UpdateKeyAsync(_systemId, created.Id, new ScimKeyUpdateRequestDto { Name = " " }));

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteKeyAsync_204AndTheKeyIsGoneAsync()
    {
        var created = Value<ApiKeyCreateResponseDto>(await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = "Entra key" })));

        var result = await CallAsync(c => c.DeleteKeyAsync(_systemId, created.Id));
        var keys = Value<IEnumerable<ApiKeyDto>>(await CallAsync(c => c.GetKeysAsync(_systemId)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<NoContentResult>());
            Assert.That(keys, Is.Empty);
        }
    }

    [Test]
    public async Task Endpoints_UnknownConnectedSystem_404Async()
    {
        var keys = await CallAsync(c => c.GetKeysAsync(int.MaxValue));
        var summary = await CallAsync(c => c.GetSummaryAsync(int.MaxValue));
        var resourceTypes = await CallAsync(c => c.GetResourceTypesAsync(int.MaxValue));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(summary, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(resourceTypes, Is.InstanceOf<NotFoundObjectResult>());
        }
    }

    [Test]
    public async Task Endpoints_ConnectedSystemUsingAnotherConnector_404Async()
    {
        int otherConnectorSystemId;
        await using (var ctx = NewContext())
        {
            var definition = await ctx.ConnectorDefinitions.FirstAsync(d => d.Name != ConnectorConstants.ScimServiceProviderConnectorName);
            var system = new ConnectedSystem { Name = $"Not SCIM {Guid.NewGuid():N}", ConnectorDefinitionId = definition.Id };
            ctx.ConnectedSystems.Add(system);
            await ctx.SaveChangesAsync();
            otherConnectorSystemId = system.Id;
        }

        var keys = await CallAsync(c => c.GetKeysAsync(otherConnectorSystemId));
        var create = await CallAsync(c => c.CreateKeyAsync(otherConnectorSystemId, new ScimKeyCreateRequestDto { Name = "Key" }));
        var summary = await CallAsync(c => c.GetSummaryAsync(otherConnectorSystemId));
        var schemas = await CallAsync(c => c.GetSchemasAsync(otherConnectorSystemId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(create, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(summary, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(schemas, Is.InstanceOf<NotFoundObjectResult>());
        }
    }

    [Test]
    public async Task GetSummaryAsync_GivesTheBaseUrlAndAnEmptyStoreAsync()
    {
        var summary = Value<ScimServiceProviderSummaryDto>(await CallAsync(c => c.GetSummaryAsync(_systemId)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.BaseUrl, Is.EqualTo($"https://jim.example.com/scim/v2/{_systemId}"));
            Assert.That(summary.ResourceCounts.Select(c => (c.ResourceTypeName, c.Count)), Is.EquivalentTo(new[] { ("User", 0), ("Group", 0) }));
            Assert.That(summary.Tombstones, Is.Zero);
            Assert.That(summary.ChangesAwaitingImport, Is.Zero);
            Assert.That(summary.ImportedUpTo, Is.Null);
            Assert.That(summary.LastWrite, Is.Null);
        }
    }

    [Test]
    public async Task GetResourceTypesAsync_TheCoreUserAndGroupAsync()
    {
        var resourceTypes = Value<IEnumerable<ScimResourceType>>(await CallAsync(c => c.GetResourceTypesAsync(_systemId))).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resourceTypes.Select(t => (t.Name, t.Endpoint)), Is.EquivalentTo(new[] { ("User", "/Users"), ("Group", "/Groups") }));
            Assert.That(resourceTypes.Single(t => t.Name == "User").SchemaExtensions.Select(e => e.Schema),
                Is.EqualTo(new[] { "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User" }));
        }
    }

    [Test]
    public async Task GetSchemasAsync_TheCoreSchemasWithTheirAttributesAsync()
    {
        var schemas = Value<IEnumerable<ScimSchema>>(await CallAsync(c => c.GetSchemasAsync(_systemId))).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schemas.Select(s => s.Id), Is.EquivalentTo(new[]
            {
                "urn:ietf:params:scim:schemas:core:2.0:User",
                "urn:ietf:params:scim:schemas:core:2.0:Group",
                "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"
            }));
            Assert.That(schemas.Single(s => s.Id == "urn:ietf:params:scim:schemas:core:2.0:User").Attributes.Select(a => a.Name), Does.Contain("userName"));
        }
    }

    [Test]
    public async Task Endpoints_FlagOff_RefusedAsync()
    {
        var created = Value<ApiKeyCreateResponseDto>(await CallAsync(c => c.CreateKeyAsync(_systemId, new ScimKeyCreateRequestDto { Name = "Entra key" })));
        await SetFlagAsync(false);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => CallAsync(c => c.GetKeysAsync(_systemId)), Throws.InstanceOf<FeatureDisabledException>());
                Assert.That(() => CallAsync(c => c.GetKeyAsync(_systemId, created.Id)), Throws.InstanceOf<FeatureDisabledException>());
                Assert.That(() => CallAsync(c => c.UpdateKeyAsync(_systemId, created.Id, new ScimKeyUpdateRequestDto { IsEnabled = false })),
                    Throws.InstanceOf<FeatureDisabledException>());
                Assert.That(() => CallAsync(c => c.DeleteKeyAsync(_systemId, created.Id)), Throws.InstanceOf<FeatureDisabledException>());
                Assert.That(() => CallAsync(c => c.GetSummaryAsync(_systemId)), Throws.InstanceOf<FeatureDisabledException>());
                Assert.That(() => CallAsync(c => c.GetResourceTypesAsync(_systemId)), Throws.InstanceOf<FeatureDisabledException>());
                Assert.That(() => CallAsync(c => c.GetSchemasAsync(_systemId)), Throws.InstanceOf<FeatureDisabledException>());
            }
        }
        finally
        {
            await SetFlagAsync(true);
        }
    }

    #region helpers

    private static T Value<T>(IActionResult result) where T : class
    {
        var value = (result as ObjectResult)?.Value as T;
        Assert.That(value, Is.Not.Null, $"Expected a {typeof(T).Name} but got {result.GetType().Name}.");
        return value!;
    }

    private async Task<IActionResult> CallAsync(Func<ScimServiceProviderController, Task<IActionResult>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("jim.example.com");
        http.User = new ClaimsPrincipal(new ClaimsIdentity(ApiKeyAuthenticationHandler.BuildApiKeyClaims(_administrator), ApiKeyAuthenticationHandler.SchemeName));
        var controller = new ScimServiceProviderController(NullLogger<ScimServiceProviderController>.Instance, jim)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return await call(controller);
    }

    private async Task<int> CreateServiceProviderSystemAsync()
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimServiceProviderConnectorName));
        var connectedSystem = new ConnectedSystem { Name = $"Entra provisioning {Guid.NewGuid():N}", ConnectorDefinitionId = definition!.Id };
        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _administrator));
        return connectedSystem.Id;
    }

    private Task SetFlagAsync(bool enabled) =>
        WithJimAsync(jim => jim.FeatureFlags.SetFeatureFlagAsync(FeatureFlagCatalogue.ScimServiceProvider.Key, enabled, _administrator, allowInDevelopment: true));

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private async Task<T> WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await call(jim);
    }

    private async Task WithJimAsync(Func<JimApplication, Task> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        await call(jim);
    }

    #endregion
}
