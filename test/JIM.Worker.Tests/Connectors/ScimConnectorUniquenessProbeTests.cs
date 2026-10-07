// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Scim.Schema;
using JIM.TestScimServiceProvider;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The SCIM 2.0 Client Connector's uniqueness probe (#1941; Unique Value Generation, #242, release 3): the filter it
/// sends and how values are escaped into it, the control value check, and how every way a service provider can fail
/// to answer becomes "could not determine" rather than "not found".
/// </summary>
[TestFixture]
public class ScimConnectorUniquenessProbeTests
{
    private readonly List<IConnectorUniquenessProbe> _opened = [];
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var connector in _opened)
            connector.CloseUniquenessProbeConnection();

        _opened.Clear();
        (_logger as IDisposable)?.Dispose();
    }

    // ---- Filter construction and escaping ----

    [Test]
    public void BuildFilter_SeveralValues_OneEqualityAssertionEachJoinedByOr()
    {
        var filter = ScimConnectorUniquenessProbe.BuildFilter("userName", ["joe.bloggs", "joe.bloggs1", "asmith"]);

        Assert.That(filter, Is.EqualTo("userName eq \"joe.bloggs\" or userName eq \"joe.bloggs1\" or userName eq \"asmith\""));
    }

    [Test]
    public void QuoteFilterValue_QuoteAndBackslash_AreEscapedAsAJsonStringRequires()
    {
        Assert.That(ScimConnectorUniquenessProbe.QuoteFilterValue("o\"brien\\x"), Is.EqualTo("\"o\\\"brien\\\\x\""));
    }

    [Test]
    public void QuoteFilterValue_ControlCharacter_IsEscapedAsAUnicodeSequence()
    {
        Assert.That(ScimConnectorUniquenessProbe.QuoteFilterValue("a\nb\u0001"), Is.EqualTo("\"a\\u000ab\\u0001\""));
    }

    [Test]
    public void QuoteFilterValue_NonAsciiCharacters_AreLeftAsTheyAre()
    {
        Assert.That(ScimConnectorUniquenessProbe.QuoteFilterValue("zoë.o'connor&co"), Is.EqualTo("\"zoë.o'connor&co\""));
    }

    /// <summary>
    /// A candidate is built from source data, so it may carry anything; it must stay one comparison value whatever it
    /// holds, never become filter grammar that widens or rewrites the search.
    /// </summary>
    [Test]
    public void BuildFilter_InjectionAttempt_StaysInsideOneComparisonValue()
    {
        var filter = ScimConnectorUniquenessProbe.BuildFilter("userName", ["x\" or userName pr or userName eq \"y"]);

        Assert.That(filter, Is.EqualTo("userName eq \"x\\\" or userName pr or userName eq \\\"y\""));
    }

    [Test]
    public void GetFilterAttributePath_CoreSimpleAttribute_IsItsName()
    {
        var attribute = new ScimFlattenedAttribute("userName", "userName", AttributeDataType.Text, AttributePlurality.SingleValued, true, AttributeWritability.Writable, "urn:ietf:params:scim:schemas:core:2.0:User");

        Assert.That(ScimConnectorUniquenessProbe.GetFilterAttributePath(attribute), Is.EqualTo("userName"));
    }

    [Test]
    public void GetFilterAttributePath_ExtensionAttribute_IsUrnQualified()
    {
        const string urn = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
        var attribute = new ScimFlattenedAttribute("enterpriseUser.employeeNumber", $"{urn}:employeeNumber", AttributeDataType.Text, AttributePlurality.SingleValued, false,
            AttributeWritability.Writable, urn, sourceAttributeName: "employeeNumber", extensionUrn: urn);

        Assert.That(ScimConnectorUniquenessProbe.GetFilterAttributePath(attribute), Is.EqualTo($"{urn}:employeeNumber"));
    }

    [Test]
    public void GetFilterAttributePath_ComplexSubAttribute_IsDotted()
    {
        var attribute = new ScimFlattenedAttribute("name.familyName", "name.familyName", AttributeDataType.Text, AttributePlurality.SingleValued, false, AttributeWritability.Writable,
            "urn:ietf:params:scim:schemas:core:2.0:User", access: ScimValueAccess.ComplexSubAttribute, sourceAttributeName: "name", subAttributeName: "familyName");

        Assert.That(ScimConnectorUniquenessProbe.GetFilterAttributePath(attribute), Is.EqualTo("name.familyName"));
    }

    /// <summary>
    /// A canonical slot (<c>emails.work</c>) is searched across every entry of the attribute, not only the slot's own
    /// type: an address held as someone's home email is still in use, and a provider is far likelier to support
    /// <c>emails.value eq</c> than a value filter.
    /// </summary>
    [Test]
    public void GetFilterAttributePath_CanonicalSlot_SearchesTheSubAttributeAcrossEveryEntry()
    {
        var attribute = new ScimFlattenedAttribute("emails.work", "emails[type eq \"work\"].value", AttributeDataType.Text, AttributePlurality.SingleValued, false,
            AttributeWritability.Writable, "urn:ietf:params:scim:schemas:core:2.0:User", access: ScimValueAccess.CanonicalSlot, sourceAttributeName: "emails",
            subAttributeName: "value", canonicalType: "work");

        Assert.That(ScimConnectorUniquenessProbe.GetFilterAttributePath(attribute), Is.EqualTo("emails.value"));
    }

    [Test]
    public void GetFilterAttributePath_Reference_CannotBeProbed()
    {
        var attribute = new ScimFlattenedAttribute("manager", "manager", AttributeDataType.Reference, AttributePlurality.SingleValued, false, AttributeWritability.Writable,
            "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", access: ScimValueAccess.ComplexReference);

        Assert.That(ScimConnectorUniquenessProbe.GetFilterAttributePath(attribute), Is.Null);
    }

    // ---- Probing a service provider ----

    [Test]
    public async Task ProbeAsync_CandidateHeldByTheProvider_IsFoundAndTheControlConfirmsTheRestAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("1", "asmith");
        provider.AddUser("2", "joe.bloggs");
        using var handler = provider.CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs", "joe.bloggs1"], "asmith"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Reason, Is.Null);
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
        }
    }

    [Test]
    public async Task ProbeAsync_OneBatch_IsOneFilteredGetAtTheResourceTypesEndpointAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("1", "asmith");
        using var handler = provider.CreateHandler();
        var connector = Open(handler);
        var requestsBefore = handler.Requests.Count;

        await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs", "joe.bloggs1"], "asmith"), _logger, CancellationToken.None);

        var probe = handler.Requests.Skip(requestsBefore).ToList();
        var query = Uri.UnescapeDataString(probe.Single().RequestUri!.Query);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.Single().Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(probe.Single().RequestUri!.AbsolutePath, Is.EqualTo("/scim/v2/Users"));
            Assert.That(query, Does.Contain("filter=userName eq \"joe.bloggs\" or userName eq \"joe.bloggs1\" or userName eq \"asmith\""));
            Assert.That(query, Does.Contain($"count={ScimConnectorUniquenessProbe.MaximumResults}"));
        }
    }

    /// <summary>
    /// The provider's own equality rules decide what counts as the same value: <c>userName</c> is not case exact, so a
    /// differently cased account is a collision.
    /// </summary>
    [Test]
    public async Task ProbeAsync_ProviderHoldsTheValueInAnotherCase_IsFoundAsync()
    {
        var provider = new MockScimProvider();
        provider.AddUser("1", "Joe.Bloggs");
        using var handler = provider.CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], null), _logger, CancellationToken.None);

        Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
    }

    [Test]
    public async Task ProbeAsync_ControlValueNotReturned_EveryCandidateUndeterminedAsync()
    {
        // The provider holds the candidate but answers as though it cannot see the control, which is how a credential
        // scoped to a subset of the provider's users looks from here.
        var provider = new MockScimProvider();
        provider.AddUser("2", "joe.bloggs");
        using var handler = provider.CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs1"], "asmith"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False, "blind to this attribute, which says nothing about the provider's others");
        }
    }

    [Test]
    public async Task ProbeAsync_GroupDisplayName_SearchesTheGroupsEndpointAsync()
    {
        var provider = new MockScimProvider();
        provider.AddGroup("g1", "Finance Team");
        provider.AddUser("1", "Finance Team 2");
        using var handler = provider.CreateHandler();
        var connector = Open(handler);
        var requestsBefore = handler.Requests.Count;

        var result = await connector.ProbeAsync(Request("Group", "displayName", ["Finance Team", "Finance Team 2"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests.Skip(requestsBefore).Single().RequestUri!.AbsolutePath, Is.EqualTo("/scim/v2/Groups"));
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }),
                "a user holding the second value is not a group, so it is no collision for a group's name");
        }
    }

    [Test]
    public async Task ProbeAsync_WorkEmailHeldAsAnotherAccountsHomeEmail_IsFoundAsync()
    {
        var provider = new MockScimProvider();
        var other = provider.AddUser("2", "someone.else");
        other.Attributes["emails"] = new[] { new Dictionary<string, object?> { ["type"] = "home", ["value"] = "joe.bloggs@example.com" } };
        using var handler = provider.CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "emails.work", ["joe.bloggs@example.com", "joe.bloggs1@example.com"], null), _logger, CancellationToken.None);

        Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
    }

    /// <summary>
    /// A provider that advertises filtering and then ignores it returns resources holding none of the values asked
    /// for. Trusting that answer would read a missing candidate as free when it may simply be on a later page.
    /// </summary>
    [Test]
    public async Task ProbeAsync_ProviderIgnoresTheFilter_UndeterminedAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.HonoursFiltering = false;
        provider.AddUser("1", "asmith");
        provider.AddUser("3", "unrelated");
        using var handler = provider.CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], "asmith"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Reason, Does.Contain("did not apply"));
        }
    }

    [Test]
    public async Task ProbeAsync_MoreMatchesThanReturned_UndeterminedAsync()
    {
        using var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)
            ? Json("""{ "totalResults": 250, "Resources": [ { "id": "1", "userName": "joe.bloggs" } ] }""")
            : NotFound());
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
        }
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task ProbeAsync_ProviderRefusesTheSearch_FailedNeverNotFoundAsync(HttpStatusCode status)
    {
        using var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal)
            ? new HttpResponseMessage(status) { Content = new StringContent(string.Empty) }
            : NotFound());
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs", "joe.bloggs1"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain(((int)status).ToString()));
        }
    }

    /// <summary>
    /// A provider that will not filter on this attribute may well filter on another, so a refused filter is reported
    /// against the attribute alone rather than stopping every probe of the provider.
    /// </summary>
    [Test]
    public async Task ProbeAsync_FilterRejected_UndeterminedForTheAttributeOnlyAsync()
    {
        var provider = new MockScimProvider();
        provider.Options.RejectsFilters = true;
        using var handler = provider.CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Reason, Does.Contain("userName"));
        }
    }

    [Test]
    public async Task ProbeAsync_ProviderUnreachable_FailedAsync()
    {
        var discovered = false;
        using var handler = new StubHttpMessageHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal))
            {
                discovered = true;
                return NotFound();
            }

            throw new HttpRequestException("Connection refused");
        });
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(discovered, Is.True);
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("could not be reached").And.Contain("Connection refused"));
        }
    }

    [Test]
    public async Task ProbeAsync_ProviderDoesNotAnswerInTime_FailedNamingTheTimeoutAsync()
    {
        using var handler = new StallingHandler();
        var connector = Open(handler);
        var request = Request("User", "userName", ["joe.bloggs"], null, TimeSpan.FromMilliseconds(200));

        var result = await connector.ProbeAsync(request, _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("did not answer within"));
        }
    }

    [Test]
    public void ProbeAsync_RunCancelled_PropagatesTheCancellation()
    {
        using var handler = new StallingHandler();
        var connector = Open(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.That(async () => await connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], null), _logger, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ProbeAsync_ResourceTypeTheProviderDoesNotPublish_UndeterminedAsync()
    {
        using var handler = new MockScimProvider().CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("Device", "displayName", ["laptop-01"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Reason, Does.Contain("Device"));
        }
    }

    [Test]
    public async Task ProbeAsync_AttributeTheProviderDoesNotPublish_UndeterminedAsync()
    {
        using var handler = new MockScimProvider().CreateHandler();
        var connector = Open(handler);

        var result = await connector.ProbeAsync(Request("User", "favouriteColour", ["green"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Reason, Does.Contain("favouriteColour"));
        }
    }

    // ---- Connection lifetime ----

    [Test]
    public void ProbeAsync_BeforeOpen_Throws()
    {
        IConnectorUniquenessProbe connector = new ScimConnector();

        Assert.That(() => connector.ProbeAsync(Request("User", "userName", ["joe.bloggs"], null), _logger, CancellationToken.None),
            Throws.InvalidOperationException);
    }

    [Test]
    public void OpenUniquenessProbeConnection_ProviderUnreachable_Throws()
    {
        using var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        var connector = new StubbedTransportScimConnector(handler);

        Assert.That(() => ((IConnectorUniquenessProbe)connector).OpenUniquenessProbeConnection(ConnectedSystem(), _logger), Throws.Exception);
    }

    [Test]
    public void CloseUniquenessProbeConnection_NeverOpened_DoesNotThrow()
    {
        IConnectorUniquenessProbe connector = new ScimConnector();

        Assert.That(connector.CloseUniquenessProbeConnection, Throws.Nothing);
    }

    [Test]
    public void CanProbeAttribute_ANamedAttribute_IsProbed()
    {
        IConnectorUniquenessProbe connector = new ScimConnector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.CanProbeAttribute("userName"), Is.True);
            Assert.That(connector.CanProbeAttribute("emails.work"), Is.True);
            Assert.That(connector.CanProbeAttribute(" "), Is.False);
        }
    }

    private IConnectorUniquenessProbe Open(HttpMessageHandler handler)
    {
        IConnectorUniquenessProbe connector = new StubbedTransportScimConnector(handler);
        connector.OpenUniquenessProbeConnection(ConnectedSystem(), _logger);
        _opened.Add(connector);
        return connector;
    }

    private static ConnectedSystem ConnectedSystem() => new()
    {
        Name = "SCIM",
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Setting = new ConnectorDefinitionSetting { Name = ScimConnectorConstants.SettingBaseUrl },
                StringValue = "https://provider.example.com/scim/v2"
            }
        ]
    };

    private static UniquenessProbeRequest Request(string objectType, string attribute, IReadOnlyList<string> candidates, string? controlValue, TimeSpan? timeout = null) => new()
    {
        ObjectTypeName = objectType,
        AttributeName = attribute,
        Candidates = candidates,
        ControlValue = controlValue,
        Timeout = timeout ?? TimeSpan.FromSeconds(30)
    };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/scim+json") };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };

    /// <summary>
    /// A provider that answers discovery (with nothing, so the core schemas apply) and then never answers a search,
    /// honouring cancellation the way a real socket handler does.
    /// </summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return NotFound();
        }
    }
}
