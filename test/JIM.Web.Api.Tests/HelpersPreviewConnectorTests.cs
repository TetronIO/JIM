// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Which built-in Connectors the portal marks with a Preview chip: those behind a Preview-tier feature flag, so an
/// administrator choosing or reading about one knows its behaviour may still change.
/// </summary>
[TestFixture]
public class HelpersPreviewConnectorTests
{
    [Test]
    public void IsPreviewConnector_ScimServiceProvider_IsTrue()
    {
        Assert.That(Helpers.IsPreviewConnector(ConnectorConstants.ScimServiceProviderConnectorName), Is.True);
    }

    [Test]
    public void IsPreviewConnector_ConnectorBehindNoFlag_IsFalse()
    {
        Assert.That(Helpers.IsPreviewConnector(ConnectorConstants.ScimClientConnectorName), Is.False);
    }
}
