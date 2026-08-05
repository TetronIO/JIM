// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// The ordered set of pattern detectors a preview asks about each of its deltas (#827 Phase 4b).
///
/// Curated and ordered, not scanned: an edit can satisfy more than one detector, so which one gets to describe it
/// is a product decision and belongs where it can be read. The first detector to recognise the change wins, and
/// <see cref="Default"/> puts them narrowest-claim first: a value whose domain or container differs only in case is
/// a casing change, and a domain cutover is a domain cutover rather than the suffix addition it also technically is.
/// </summary>
public class PreviewPatternDetectorRegistry
{
    /// <summary>
    /// The detectors every preview runs. Order is the precedence described above; adding a detector means deciding
    /// where in this list it belongs, which is the point of writing it out.
    /// </summary>
    public static PreviewPatternDetectorRegistry Default { get; } = new(
    [
        new CasingChangeDetector(),
        new EmailDomainChangeDetector(),
        new ContainerChangeDetector(),
        new AffixChangeDetector()
    ]);

    private readonly IReadOnlyList<IPreviewPatternDetector> _detectors;

    public PreviewPatternDetectorRegistry(IReadOnlyList<IPreviewPatternDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);

        if (detectors.Count == 0)
        {
            // An empty registry labels nothing, which looks exactly like every detector declining. Refusing it here
            // turns a silently featureless preview into a failure at construction.
            throw new ArgumentException(
                "A preview pattern detector registry needs at least one detector; an empty one would silently recognise nothing.",
                nameof(detectors));
        }

        _detectors = detectors;
    }

    /// <summary>
    /// The pattern the first detector to recognise <paramref name="candidate"/> gives it, or null where none did.
    /// </summary>
    public string? Detect(PreviewPatternCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        foreach (var detector in _detectors)
        {
            try
            {
                var key = detector.Detect(candidate);
                if (key != null)
                    return key;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A pattern is a label on a group that is already complete and already counted exactly. Letting a
                // bug in one detector propagate would fail an otherwise correct preview of tens of thousands of
                // objects over a piece of decoration, so the detector is skipped and the rest still get their turn.
                // Cancellation is excluded deliberately: an aborting preview must stop, not grind on through the
                // rest of the registry. The candidate is not logged: its values are attribute values, and
                // therefore personal data.
                Log.Warning(ex, "Preview pattern detector {DetectorType} threw and was skipped for one delta", detector.GetType().Name);
            }
        }

        return null;
    }
}
