// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// Works out the watermark the next Delta Import should ask for changes after, accumulating across
/// every page of one run.
/// <para>
/// The watermark comes from the service provider's own clock at the moment the run started reading,
/// not from the data: the newest <c>meta.lastModified</c> in a directory that has stopped changing
/// never advances, so a data-derived watermark would have every delta re-import the whole system for
/// ever. The provider's <c>Date</c> response header is used, which is the only reading of its clock
/// the protocol guarantees.
/// </para>
/// </summary>
internal sealed class ScimWatermarkTracker
{
    /// <summary>
    /// How far behind the start of the run the watermark is stored. It absorbs two things: the
    /// one-second precision providers publish <c>meta.lastModified</c> at, which would otherwise let a
    /// change made in the same second slip between two runs, and any modest disagreement between the
    /// clock serving the <c>Date</c> header and the clock stamping resource metadata. Re-reading an
    /// unchanged resource costs a request and nothing else; missing a change is silent divergence, so
    /// the overlap is deliberate.
    /// </summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _providerClockAtRunStart;
    private DateTimeOffset? _highestLastModified;

    /// <summary>
    /// Records the provider's clock from a page response. The first reading wins: it is the closest
    /// thing the run has to the instant it began reading, and anything changed after that point must
    /// be left for the next run.
    /// </summary>
    /// <param name="providerClock">The response's <c>Date</c> header, or null when it sent none.</param>
    public void ObserveProviderClock(DateTimeOffset? providerClock)
    {
        if (providerClock.HasValue && !_providerClockAtRunStart.HasValue)
            _providerClockAtRunStart = providerClock;
    }

    /// <summary>
    /// Records a resource's last-modified date, used only as the fallback watermark source for a
    /// provider that sends no <c>Date</c> header.
    /// </summary>
    public void ObserveLastModified(DateTimeOffset lastModified)
    {
        if (!_highestLastModified.HasValue || lastModified > _highestLastModified.Value)
            _highestLastModified = lastModified;
    }

    /// <summary>
    /// The watermark to persist, or null when the run learned nothing to base one on, in which case
    /// the previously stored watermark stands.
    /// </summary>
    public DateTimeOffset? Resolve()
    {
        var basis = _providerClockAtRunStart ?? _highestLastModified;
        return basis.HasValue ? basis.Value.ToUniversalTime() - SafetyMargin : null;
    }
}
