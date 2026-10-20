// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests.Scim;

/// <summary>
/// The SCIM store summary as the REST API returns it.
/// </summary>
[TestFixture]
public class ScimServiceProviderSummaryDtoTests
{
    [Test]
    public void FromSummary_RecentRejections_Carried()
    {
        var dto = ScimServiceProviderSummaryDto.FromSummary(new ScimStoreSummary { RejectedLast24Hours = 4 }, "https://jim.example.com/scim/v2/7");

        Assert.That(dto.RejectedLast24Hours, Is.EqualTo(4));
    }
}
