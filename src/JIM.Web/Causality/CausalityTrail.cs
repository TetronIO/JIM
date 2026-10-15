// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The foci that led to the one the Causality Explorer (#2100) has in view, oldest first, carried in the page address as
/// the <c>trail</c> query value: comma-separated <see cref="CausalityFocus.Token"/>s.
/// </summary>
/// <remarks>
/// <para>
/// It is read from whatever an address holds, so it never throws: a token it cannot read is dropped and the rest kept.
/// </para>
/// <para>
/// It holds at most <see cref="MaxFoci"/> foci counting the one in view, about 400 characters of address, dropping the
/// oldest past that. Following a focus already on it returns there, dropping what came after it, as browser history
/// drops the pages ahead of one navigated back to; so no focus is ever listed twice.
/// </para>
/// </remarks>
public sealed class CausalityTrail : IEquatable<CausalityTrail>
{
    /// <summary>
    /// The most foci a trail holds, counting the one in view.
    /// </summary>
    public const int MaxFoci = 10;

    public static readonly CausalityTrail Empty = new([]);

    private CausalityTrail(IReadOnlyList<CausalityFocus> foci) => Foci = foci;

    /// <summary>
    /// The foci before the one in view, oldest first.
    /// </summary>
    public IReadOnlyList<CausalityFocus> Foci { get; }

    /// <summary>
    /// Reads the trail an address carries to <paramref name="current"/>.
    /// </summary>
    public static CausalityTrail Parse(string? value, CausalityFocus current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(value))
            return Empty;

        var foci = value.Split(',')
            .Select(token => CausalityFocus.TryParse(token, out var focus) ? focus : null)
            .Where(focus => focus != null)
            .Select(focus => focus!)
            .ToList();

        return new CausalityTrail(EndBefore(foci, current));
    }

    /// <summary>
    /// The trail that leads to <paramref name="next"/> when it is followed from <paramref name="current"/>.
    /// </summary>
    public CausalityTrail Follow(CausalityFocus current, CausalityFocus next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        return new CausalityTrail(EndBefore([.. Foci, current], next));
    }

    /// <summary>
    /// The trail as the address writes it, or null where it is empty.
    /// </summary>
    public string? Serialise() => Foci.Count == 0 ? null : string.Join(",", Foci.Select(f => f.Token));

    /// <summary>
    /// The explorer's address for <paramref name="focus"/>, carrying this trail.
    /// </summary>
    public string Href(CausalityFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        return Serialise() is { } trail ? $"{focus.Path}?trail={trail}" : focus.Path;
    }

    /// <summary>
    /// The address that returns to the focus at <paramref name="index"/>, carrying the trail that led to it.
    /// </summary>
    public string CrumbHref(int index) => new CausalityTrail(Foci.Take(index).ToList()).Href(Foci[index]);

    public bool Equals(CausalityTrail? other) => other != null && Foci.SequenceEqual(other.Foci);

    public override bool Equals(object? obj) => Equals(obj as CausalityTrail);

    public override int GetHashCode() => Serialise()?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>
    /// The foci before <paramref name="focus"/>: up to where it already stands, else all of them, keeping the most recent
    /// that fit beside it.
    /// </summary>
    private static List<CausalityFocus> EndBefore(List<CausalityFocus> foci, CausalityFocus focus)
    {
        var index = foci.IndexOf(focus);
        var before = index >= 0 ? foci.Take(index) : foci;
        return before.TakeLast(MaxFoci - 1).ToList();
    }
}
