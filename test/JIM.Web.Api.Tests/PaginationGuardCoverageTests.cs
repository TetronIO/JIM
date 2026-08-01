// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JIM.Web.Controllers.Api;
using JIM.Web.Middleware.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Completeness guard for the pagination depth cap (issue #487).
/// <para>
/// The cap originally lived only on <see cref="PaginationRequest"/>, so it reached only the endpoints that
/// bound that type. Nine paginated endpoints took bare page / pageSize query parameters instead and were
/// silently unprotected, including the connector space and Pending Export lists (the largest tables JIM has).
/// Nothing failed; the gap was invisible until read for.
/// </para>
/// <para>
/// These tests walk every API action by reflection and assert that each paginated one is a shape the guard
/// actually recognises, so a new endpoint cannot reintroduce the gap without failing the build.
/// </para>
/// </summary>
[TestFixture]
public class PaginationGuardCoverageTests
{
    private static IEnumerable<MethodInfo> GetApiActions()
    {
        return typeof(MetaverseController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());
    }

    // A parameter that carries a page number, whatever it happens to be called.
    private static bool IsPageNumberParameter(ParameterInfo parameter)
    {
        if (parameter.ParameterType != typeof(int) && parameter.ParameterType != typeof(int?))
            return false;

        var name = parameter.Name ?? string.Empty;
        return name.Contains("page", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("pageSize", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public void EveryPaginatedAction_ExposesAPageParameterTheDepthGuardRecognises()
    {
        // The filter resolves the page number by argument name. An action that spells it "pageNumber" or
        // "startPage" would bind fine, serve requests, and never be guarded; this test is the only thing that
        // would notice.
        var unrecognised = GetApiActions()
            .SelectMany(action => action.GetParameters()
                .Where(IsPageNumberParameter)
                .Where(p => !PaginationDepthActionFilter.IsRecognisedPageParameterName(p.Name!))
                .Select(p => $"{action.DeclaringType?.Name}.{action.Name}({p.Name})"))
            .ToList();

        Assert.That(unrecognised, Is.Empty,
            "These actions take a page number the depth guard does not recognise. Rename the parameter to 'page', " +
            "or bind a PaginationRequest instead, so the cap applies: " + string.Join(", ", unrecognised));
    }

    [Test]
    public void EveryPaginatedAction_AlsoTakesAPageSizeOrBindsAPaginationRequest()
    {
        // Depth is page x pageSize. An action with a page but no page size makes the guard fall back to the
        // maximum page size, which is safe but blunt; flag it so the author makes a deliberate choice.
        var pageWithoutPageSize = GetApiActions()
            .Where(action => action.GetParameters().Any(IsPageNumberParameter))
            .Where(action => !action.GetParameters().Any(p =>
                string.Equals(p.Name, "pageSize", StringComparison.OrdinalIgnoreCase)))
            .Select(action => $"{action.DeclaringType?.Name}.{action.Name}")
            .ToList();

        Assert.That(pageWithoutPageSize, Is.Empty,
            "These actions accept a page number but no page size: " + string.Join(", ", pageWithoutPageSize));
    }

    [Test]
    public void EveryPaginatedAction_ClampsPageSizeToTheSharedMaximum()
    {
        // The depth rule assumes the page size can never exceed PaginationLimits.MaxPageSize. Any action that
        // documents a larger page size would break that assumption.
        var maxPageSize = PaginationLimits.MaxPageSize;
        Assert.That(maxPageSize, Is.EqualTo(100),
            "The bare-parameter endpoints clamp page size to 100 in-method; the shared constant must agree.");
    }

    [Test]
    public void PaginatedActionsExist_SoTheseTestsAreNotVacuous()
    {
        // A reflection test that silently matches nothing passes forever. Assert the corpus is non-trivial.
        var paginatedActionCount = GetApiActions()
            .Count(action => action.GetParameters().Any(p =>
                IsPageNumberParameter(p) || p.ParameterType == typeof(PaginationRequest)));

        Assert.That(paginatedActionCount, Is.GreaterThan(10),
            "Expected the API to expose many paginated actions; if this fails the reflection query is wrong.");
    }
}
