// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Page request construction and the position that travels between the repeated import calls JIM makes.
/// </summary>
[TestFixture]
public class ScimPaginationTests
{
    #region query building
    [Test]
    public void BuildPageQuery_IndexPaging_AsksForTheCountAndStartIndex()
    {
        var position = new ScimImportPosition { Mode = ScimPaginationMode.Index, StartIndex = 201 };

        var query = ScimQueryBuilder.BuildPageQuery("/Users", position, pageSize: 100);

        Assert.That(query, Is.EqualTo("Users?count=100&startIndex=201"));
    }

    [Test]
    public void BuildPageQuery_LeadingSlashOnTheEndpoint_IsTrimmedSoTheBaseUrlPrefixSurvives()
    {
        // Providers publish endpoints with a leading slash (RFC 7643 section 6). Composed as-is against
        // https://host/scim/v2/ it would resolve to https://host/Users and lose the prefix entirely.
        var query = ScimQueryBuilder.BuildPageQuery("/Users", new ScimImportPosition(), pageSize: 10);

        Assert.That(query, Does.StartWith("Users?"));
    }

    [Test]
    public void BuildPageQuery_CursorPaging_SendsAnEmptyCursorOnTheFirstPage()
    {
        // RFC 9865: an empty cursor is how a client asks for cursor paging.
        var position = new ScimImportPosition { Mode = ScimPaginationMode.Cursor };

        var query = ScimQueryBuilder.BuildPageQuery("/Users", position, pageSize: 50);

        Assert.That(query, Is.EqualTo("Users?count=50&cursor="));
    }

    [Test]
    public void BuildPageQuery_CursorPaging_SendsTheProvidersCursorOnLaterPages()
    {
        var position = new ScimImportPosition { Mode = ScimPaginationMode.Cursor, Cursor = "eyJpZCI6IjEwIn0=" };

        var query = ScimQueryBuilder.BuildPageQuery("/Users", position, pageSize: 50);

        Assert.That(query, Is.EqualTo("Users?count=50&cursor=eyJpZCI6IjEwIn0%3D"));
    }

    [Test]
    public void BuildPageQuery_CursorPaging_NeverSendsAStartIndex()
    {
        // Sending both invites a provider to honour the wrong one and repeat or skip a page.
        var position = new ScimImportPosition { Mode = ScimPaginationMode.Cursor, Cursor = "abc", StartIndex = 501 };

        var query = ScimQueryBuilder.BuildPageQuery("/Users", position, pageSize: 50);

        Assert.That(query, Does.Not.Contain("startIndex"));
    }

    [Test]
    public void BuildPageQuery_Filter_IsPercentEncoded()
    {
        var query = ScimQueryBuilder.BuildPageQuery("/Users", new ScimImportPosition(), pageSize: 10,
            filter: "meta.lastModified gt \"2026-01-01T00:00:00Z\"");

        Assert.That(query, Does.Contain("filter=meta.lastModified%20gt%20%222026-01-01T00%3A00%3A00Z%22"));
    }

    [Test]
    public void BuildPageQuery_ExcludedAttributes_AreSentAsOneCommaSeparatedParameter()
    {
        var query = ScimQueryBuilder.BuildPageQuery("/Users", new ScimImportPosition(), pageSize: 10,
            excludedAttributes: ["photos", "x509Certificates"]);

        Assert.That(query, Does.Contain("excludedAttributes=photos%2Cx509Certificates"));
    }

    [Test]
    public void BuildPageQuery_NoExcludedAttributes_SendsNoSuchParameter()
    {
        var query = ScimQueryBuilder.BuildPageQuery("/Users", new ScimImportPosition(), pageSize: 10, excludedAttributes: []);

        Assert.That(query, Does.Not.Contain("excludedAttributes"));
    }

    [Test]
    public void BuildPageQuery_NoPageSize_OmitsCountAndLetsTheProviderDecide()
    {
        var query = ScimQueryBuilder.BuildPageQuery("/Users", new ScimImportPosition(), pageSize: 0);

        Assert.That(query, Is.EqualTo("Users?startIndex=1"));
    }

    [TestCase("photos, x509Certificates", 2)]
    [TestCase("photos\nx509Certificates", 2)]
    [TestCase("photos;x509Certificates", 2)]
    [TestCase("photos,photos", 1)]
    [TestCase("", 0)]
    [TestCase(null, 0)]
    public void ParseExcludedAttributes_AcceptsTheSeparatorsAnAdministratorMightUse(string? setting, int expectedCount)
    {
        Assert.That(ScimQueryBuilder.ParseExcludedAttributes(setting), Has.Count.EqualTo(expectedCount));
    }
    #endregion

    #region position round trip
    [Test]
    public void FromTokens_NoTokens_StartsAtTheFirstResourceType()
    {
        var position = ScimImportPosition.FromTokens([], ScimPaginationMode.Auto);

        Assert.Multiple(() =>
        {
            Assert.That(position.ResourceTypeIndex, Is.Zero);
            // RFC 7644 numbers resources from 1.
            Assert.That(position.StartIndex, Is.EqualTo(1));
            Assert.That(position.Cursor, Is.Null);
        });
    }

    [Test]
    public void FromTokens_AutoMode_OpensIndexBasedBecauseThatIsTheMandatoryStyle()
    {
        // A provider that rejects or ignores an unknown cursor parameter would fail the very first page.
        var position = ScimImportPosition.FromTokens([], ScimPaginationMode.Auto);

        Assert.That(position.Mode, Is.EqualTo(ScimPaginationMode.Index));
    }

    [Test]
    public void FromTokens_CursorModeChosenExplicitly_OpensCursorBased()
    {
        var position = ScimImportPosition.FromTokens([], ScimPaginationMode.Cursor);

        Assert.That(position.Mode, Is.EqualTo(ScimPaginationMode.Cursor));
    }

    [Test]
    public void ToToken_ThenFromTokens_PreservesTheWholePosition()
    {
        var original = new ScimImportPosition
        {
            ResourceTypeIndex = 1,
            StartIndex = 301,
            Cursor = "eyJpZCI6IjEwIn0=",
            Mode = ScimPaginationMode.Cursor
        };

        var restored = ScimImportPosition.FromTokens([original.ToToken()], ScimPaginationMode.Auto);

        Assert.Multiple(() =>
        {
            Assert.That(restored.ResourceTypeIndex, Is.EqualTo(1));
            Assert.That(restored.StartIndex, Is.EqualTo(301));
            Assert.That(restored.Cursor, Is.EqualTo("eyJpZCI6IjEwIn0="));
            Assert.That(restored.Mode, Is.EqualTo(ScimPaginationMode.Cursor));
        });
    }

    [Test]
    public void FromTokens_UnreadableToken_FailsRatherThanRestartingTheImportSilently()
    {
        // Starting over partway through would look like a successful run that imported a fraction of the
        // data, which is exactly the silent divergence sync integrity forbids.
        var tokens = new List<ConnectedSystemPaginationToken> { new(ScimImportPosition.TokenName, "not json") };

        Assert.That(() => ScimImportPosition.FromTokens(tokens, ScimPaginationMode.Auto),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void FromTokens_TokensFromAnotherConnector_AreIgnored()
    {
        var tokens = new List<ConnectedSystemPaginationToken> { new("SomeOtherToken", "value") };

        var position = ScimImportPosition.FromTokens(tokens, ScimPaginationMode.Auto);

        Assert.That(position.StartIndex, Is.EqualTo(1));
    }
    #endregion
}
