// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Connectors.MockScim;

/// <summary>
/// The ways <see cref="MockScimProvider"/> can behave. Every switch here corresponds to a deviation
/// real service providers exhibit; the defaults describe a well-behaved, RFC-conformant provider, and a
/// test turns on exactly the misbehaviour it is about.
/// </summary>
internal sealed class MockScimProviderOptions
{
    /// <summary>The provider's own clock, which its <c>Date</c> response header reports.</summary>
    public DateTimeOffset ProviderClock { get; set; } = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How far the clock serving the <c>Date</c> header runs ahead of (or behind) the clock stamping
    /// resource metadata. Non-zero models a gateway in front of the SCIM application.
    /// </summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>Whether a <c>Date</c> response header is sent at all.</summary>
    public bool SendsDateHeader { get; set; } = true;

    public bool PublishesServiceProviderConfig { get; set; } = true;

    public bool PublishesResourceTypes { get; set; } = true;

    /// <summary>Plenty of providers serve no <c>/Schemas</c>, leaving the client on the RFC 7643 core schemas.</summary>
    public bool PublishesSchemas { get; set; } = true;

    /// <summary>What <c>/ServiceProviderConfig</c> claims about PATCH, and whether PATCH is honoured.</summary>
    public bool SupportsPatch { get; set; } = true;

    /// <summary>
    /// Rejects every create with 400 <c>invalidValue</c>, as a provider does when the resource refers to
    /// something that has not been created yet. RFC 7644 makes ordering the client's responsibility, so
    /// this is the shape a dependency-ordering problem arrives in.
    /// </summary>
    public bool RejectsCreateWithMissingDependency { get; set; }

    /// <summary>What <c>/ServiceProviderConfig</c> claims about entity tags.</summary>
    public bool SupportsETag { get; set; } = true;

    /// <summary>
    /// Changes a resource's entity tag the moment it is read, so the write that follows carries a stale
    /// one. Models another client changing the resource in the window between JIM reading it and writing
    /// it back, which is the lost update If-Match exists to prevent.
    /// </summary>
    public bool ChangesVersionBetweenReadAndWrite { get; set; }

    /// <summary>What <c>/ServiceProviderConfig</c> claims about filtering.</summary>
    public bool AdvertisesFiltering { get; set; } = true;

    /// <summary>
    /// Whether a filter is actually applied. False models a provider that advertises filtering and then
    /// returns everything regardless, which is a silent correctness problem rather than a loud one.
    /// </summary>
    public bool HonoursFiltering { get; set; } = true;

    /// <summary>
    /// Whether any filter is rejected outright with 400 <c>invalidFilter</c>, which is what a provider
    /// advertising a capability it does not have usually does in practice.
    /// </summary>
    public bool RejectsFilters { get; set; }

    public MockScimPaginationStyle Pagination { get; set; } = MockScimPaginationStyle.Index;

    /// <summary>
    /// A cap the provider imposes on page size regardless of the requested <c>count</c>. A client that
    /// advanced by what it asked for rather than by what it got would skip resources.
    /// </summary>
    public int? MaximumPageSize { get; set; }

    /// <summary>How many of the first resource requests answer 429 with a <c>Retry-After</c>.</summary>
    public int ThrottleFirstCalls { get; set; }

    /// <summary>When set, requests without this bearer token are answered 401.</summary>
    public string? RequiredBearerToken { get; set; }

    /// <summary>Returns a 200 whose body is not valid JSON, as providers behind broken gateways do.</summary>
    public bool ReturnsMalformedBody { get; set; }

    /// <summary>
    /// Names the ListResponse members in lower case (<c>resources</c>, <c>totalresults</c>). RFC 7643
    /// section 2.1 makes attribute names case insensitive, so this is conformant, and real providers do
    /// it; a client matching member names by exact case would read every page as empty.
    /// </summary>
    public bool UsesLowerCaseMemberNames { get; set; }

    /// <summary>
    /// The resource request number to answer with a 500. A transient server error part way through a
    /// walk must fail the run, never end it quietly: a truncated import reads as a successful one that
    /// found fewer objects, which deletion detection would then act on.
    /// </summary>
    public int? FailWithServerErrorOnRequest { get; set; }

    /// <summary>
    /// Repeats the previous page's last resource at the top of the next page. Index paging over a
    /// collection that gains a resource ahead of the current position shifts the window, and real
    /// providers hand the same resource out twice as a result.
    /// </summary>
    public bool RepeatsTheLastResourceOnEachPage { get; set; }

    /// <summary>Omits <c>totalResults</c>, which some providers do despite RFC 7644 requiring it.</summary>
    public bool OmitsTotalResults { get; set; }

    /// <summary>
    /// Reports the page's size as <c>totalResults</c> rather than the collection's. An easy provider
    /// mistake, and one a client that trusts the total would silently truncate on.
    /// </summary>
    public bool ReportsPageSizeAsTotalResults { get; set; }

    /// <summary>
    /// Returns a bare JSON array instead of a ListResponse envelope. The danger is not the deviation
    /// itself but the shape of the failure: an envelope that deserialises to nothing would read as a
    /// successful import of zero resources.
    /// </summary>
    public bool ReturnsBareArray { get; set; }
}
