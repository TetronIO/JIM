// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Causality Explorer is an Administrator's diagnostic, as the REST endpoints it shares its data with already
/// are: a reader in the User role alone cannot open it, whichever address they reach it by.
/// </summary>
[TestFixture]
public class CausalityExplorerAuthorisationTests
{
    [Test]
    public void CausalityExplorer_IsRestrictedToAdministrators()
    {
        var authorise = typeof(CausalityExplorer).GetCustomAttribute<AuthorizeAttribute>();

        Assert.That(authorise?.Roles, Is.EqualTo("Administrator"));
    }
}
