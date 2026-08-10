// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using JIM.Web.Extensions.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Guards JIM's API versioning configuration and the precondition that justifies it.
/// <para>
/// JIM versions by URL path segment and deliberately does not set
/// <see cref="ApiVersioningOptions.AssumeDefaultVersionWhenUnspecified"/>, because every routable API controller
/// declares its own <c>[ApiVersion]</c>. That makes the setting dead configuration; it also makes the absence of
/// an <c>[ApiVersion]</c> on a newly added controller a runtime failure rather than a build failure, which is
/// what these tests exist to prevent.
/// </para>
/// </summary>
[TestFixture]
public class ApiVersioningConfigurationTests
{
    [Test]
    public void EveryRoutableApiController_DeclaresAnApiVersion()
    {
        var undeclared = RoutableApiControllers()
            .Where(controller => !controller.GetCustomAttributes<ApiVersionAttribute>(inherit: true).Any())
            .Select(controller => controller.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.That(undeclared, Is.Empty,
            "Every routable API controller must declare [ApiVersion], because JIM does not set " +
            "AssumeDefaultVersionWhenUnspecified: there is no default to fall back on, so an undeclared " +
            "controller fails to match its route at runtime while the build stays clean.");
    }

    [Test]
    public void AddJimApiVersioning_DoesNotAssumeADefaultVersion()
    {
        var options = BuildVersioningOptions();

        Assert.That(options.AssumeDefaultVersionWhenUnspecified, Is.False,
            "Assuming a default version is only meaningful for endpoints that carry no version metadata. " +
            "EveryRoutableApiController_DeclaresAnApiVersion asserts that no such endpoint exists, so enabling " +
            "this would be dead configuration.");
    }

    [Test]
    public void AddJimApiVersioning_VersionsByUrlSegment_AndReportsVersions()
    {
        var options = BuildVersioningOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.ApiVersionReader, Is.TypeOf<UrlSegmentApiVersionReader>(),
                "Routes are templated 'api/v{version:apiVersion}/[controller]', so the version is read from the " +
                "URL segment rather than a header or query string.");
            Assert.That(options.DefaultApiVersion, Is.EqualTo(new ApiVersion(1, 0)));
            Assert.That(options.ReportApiVersions, Is.True);
        }
    }

    /// <summary>
    /// Pins that the controller pipeline is opted into versioning at all, whichever call provides it.
    /// <para>
    /// Note what this does <b>not</b> catch: dropping <c>AddMvc()</c> alone leaves these registrations in place,
    /// because <c>AddApiExplorer()</c> registers a superset of them. The guard against that is the AV0013
    /// analyser, which fails the build. This test covers the wider regression of the versioning block being
    /// rewired or removed, which no analyser would notice.
    /// </para>
    /// </summary>
    [Test]
    public void AddJimApiVersioning_OptsTheControllerPipelineIntoVersioning()
    {
        var services = BuildServices();

        var mvcVersioningServices = services
            .Where(descriptor => (descriptor.ImplementationType ?? descriptor.ServiceType)
                .Assembly.GetName().Name == "Asp.Versioning.Mvc")
            .ToList();

        Assert.That(mvcVersioningServices, Is.Not.Empty,
            "Controllers are discovered and routed by MVC, which has to be opted into API versioning for the " +
            "[ApiVersion] metadata on a controller to reach the actions it declares.");
    }

    /// <summary>
    /// The API explorer is what groups the versioned controllers into the OpenAPI document, and
    /// <c>SubstituteApiVersionInUrl</c> is what turns the <c>{version}</c> route token into a literal <c>v1</c>
    /// in that document. Without it every documented path reads <c>/api/v{version}/...</c>.
    /// </summary>
    [Test]
    public void AddJimApiVersioning_SubstitutesTheVersionIntoDocumentedUrls()
    {
        var provider = BuildServices().BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ApiExplorerOptions>>().Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.SubstituteApiVersionInUrl, Is.True);
            Assert.That(options.GroupNameFormat, Is.EqualTo("'v'VVV"));
        }
    }

    private static ApiVersioningOptions BuildVersioningOptions() =>
        BuildServices().BuildServiceProvider().GetRequiredService<IOptions<ApiVersioningOptions>>().Value;

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        services.AddJimApiVersioning();
        return services;
    }

    /// <summary>
    /// Every concrete controller that ASP.NET will route. Abstract base classes are excluded: they declare no
    /// routes of their own, and their derived types carry the attributes.
    /// </summary>
    private static IEnumerable<Type> RoutableApiControllers() =>
        typeof(JIM.Web.Controllers.Api.SynchronisationController).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(type));
}
