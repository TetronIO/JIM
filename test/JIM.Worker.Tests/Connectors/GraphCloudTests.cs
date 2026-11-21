// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphCloudTests
{
    [TestCase(GraphConnectorConstants.CloudGlobal, "https://login.microsoftonline.com/", "https://graph.microsoft.com/")]
    [TestCase(GraphConnectorConstants.CloudUsGovernmentL4, "https://login.microsoftonline.us/", "https://graph.microsoft.us/")]
    [TestCase(GraphConnectorConstants.CloudUsGovernmentL5, "https://login.microsoftonline.us/", "https://dod-graph.microsoft.us/")]
    [TestCase(GraphConnectorConstants.CloudChina, "https://login.chinacloudapi.cn/", "https://microsoftgraph.chinacloudapi.cn/")]
    public void TryResolve_KnownCloud_ReturnsItsTokenServiceAndGraphHost(string name, string authorityHost, string graphHost)
    {
        var resolved = GraphCloud.TryResolve(name, out var cloud);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(cloud.AuthorityHost.AbsoluteUri, Is.EqualTo(authorityHost));
            Assert.That(cloud.GraphHost.AbsoluteUri, Is.EqualTo(graphHost));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void TryResolve_NoValue_ReturnsGlobal(string? name)
    {
        var resolved = GraphCloud.TryResolve(name, out var cloud);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(cloud, Is.EqualTo(GraphCloud.Global));
        }
    }

    [Test]
    public void TryResolve_UnknownCloud_ReturnsFalse()
    {
        Assert.That(GraphCloud.TryResolve("Germany", out _), Is.False);
    }

    [Test]
    public void TryResolve_DifferentCase_Resolves()
    {
        var resolved = GraphCloud.TryResolve("us government l5 (dod)", out var cloud);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(cloud, Is.EqualTo(GraphCloud.UsGovernmentL5));
        }
    }

    [Test]
    public void Scope_IsTheCloudsGraphHostWithDefaultSuffix()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GraphCloud.Global.Scope, Is.EqualTo("https://graph.microsoft.com/.default"));
            Assert.That(GraphCloud.UsGovernmentL5.Scope, Is.EqualTo("https://dod-graph.microsoft.us/.default"));
        }
    }

    [Test]
    public void ApiBaseUrl_IsTheV1Endpoint()
    {
        Assert.That(GraphCloud.UsGovernmentL4.ApiBaseUrl.AbsoluteUri, Is.EqualTo("https://graph.microsoft.us/v1.0/"));
    }

    [Test]
    public void TokenEndpoint_IsTheTenantsV2EndpointOnTheCloudsTokenService()
    {
        var endpoint = GraphCloud.China.TokenEndpoint("contoso.partner.onmschina.cn");

        Assert.That(endpoint.AbsoluteUri, Is.EqualTo("https://login.chinacloudapi.cn/contoso.partner.onmschina.cn/oauth2/v2.0/token"));
    }

    [Test]
    public void All_NamesMatchTheCloudDropDownValues()
    {
        Assert.That(GraphCloud.All.Select(c => c.Name), Is.EqualTo(new[]
        {
            GraphConnectorConstants.CloudGlobal,
            GraphConnectorConstants.CloudUsGovernmentL4,
            GraphConnectorConstants.CloudUsGovernmentL5,
            GraphConnectorConstants.CloudChina
        }));
    }
}
