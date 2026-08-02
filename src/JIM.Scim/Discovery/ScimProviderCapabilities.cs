// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Discovery;

/// <summary>
/// What a service provider can actually do, resolved from its <c>/ServiceProviderConfig</c> and used to
/// decide how to import and export against it.
/// <para>
/// Every capability defaults to unsupported. A provider that does not advertise a feature has not
/// promised it, and guessing optimistically turns a discovery gap into failed exports; the floors
/// (full scan for import, PUT for update) always work.
/// </para>
/// </summary>
public class ScimProviderCapabilities
{
    /// <summary>
    /// Whether <c>/ServiceProviderConfig</c> answered. When it did not, every capability below is at its
    /// floor and <see cref="Warnings"/> says so, which the run reports rather than absorbing silently.
    /// </summary>
    public bool DiscoveryAvailable { get; private init; }

    /// <summary>
    /// Whether PATCH is supported. When it is not, updates degrade to whole-resource PUT.
    /// </summary>
    public bool SupportsPatch { get; private init; }

    public bool SupportsBulk { get; private init; }

    /// <summary>The provider's stated cap on operations per bulk request, where it states one.</summary>
    public int? BulkMaxOperations { get; private init; }

    /// <summary>The provider's stated cap on bulk payload size in bytes, where it states one.</summary>
    public long? BulkMaxPayloadSize { get; private init; }

    /// <summary>
    /// Whether filtering is supported, which is what makes delta import by <c>meta.lastModified</c>
    /// possible at all.
    /// </summary>
    public bool SupportsFilter { get; private init; }

    /// <summary>
    /// The provider's cap on results returned for a filtered query, which pagination must respect or
    /// later pages are silently truncated.
    /// </summary>
    public int? FilterMaxResults { get; private init; }

    /// <summary>Whether entity tags are maintained, which the ETag change-detection strategy requires.</summary>
    public bool SupportsETag { get; private init; }

    public bool SupportsSort { get; private init; }

    public bool SupportsChangePassword { get; private init; }

    /// <summary>
    /// The authentication scheme keywords the provider advertises. Advisory only: at least one real
    /// provider advertises OAuth Bearer while enforcing nothing, so JIM never infers its credential
    /// choice from this.
    /// </summary>
    public List<string> AuthenticationSchemes { get; private init; } = [];

    /// <summary>
    /// Discovery shortfalls worth telling an administrator about, surfaced as run warnings. Never a
    /// reason to fail: a provider can be perfectly usable at the protocol floors.
    /// </summary>
    public List<string> Warnings { get; private init; } = [];

    /// <summary>
    /// Resolves capabilities from a discovery document, or from its absence.
    /// </summary>
    /// <param name="config">The provider's configuration, or null when the endpoint did not answer.</param>
    public static ScimProviderCapabilities From(ScimServiceProviderConfig? config)
    {
        if (config == null)
        {
            return new ScimProviderCapabilities
            {
                DiscoveryAvailable = false,
                Warnings =
                [
                    "The service provider did not return a ServiceProviderConfig document, so its optional capabilities are unknown. " +
                    "JIM will use the protocol floors: full-scan import and whole-resource PUT updates."
                ]
            };
        }

        var warnings = new List<string>();
        var supportsFilter = config.Filter?.Supported == true;
        var supportsPatch = config.Patch?.Supported == true;

        if (!supportsFilter)
            warnings.Add("The service provider does not advertise filtering, so delta import cannot query by last-modified date. Imports will be full scans.");
        if (!supportsPatch)
            warnings.Add("The service provider does not advertise PATCH, so updates will be sent as whole-resource PUT requests, which overwrite attributes JIM does not manage.");

        return new ScimProviderCapabilities
        {
            DiscoveryAvailable = true,
            SupportsPatch = supportsPatch,
            SupportsBulk = config.Bulk?.Supported == true,
            BulkMaxOperations = config.Bulk?.Supported == true ? config.Bulk.MaxOperations : null,
            BulkMaxPayloadSize = config.Bulk?.Supported == true ? config.Bulk.MaxPayloadSize : null,
            SupportsFilter = supportsFilter,
            FilterMaxResults = supportsFilter ? config.Filter?.MaxResults : null,
            SupportsETag = config.ETag?.Supported == true,
            SupportsSort = config.Sort?.Supported == true,
            SupportsChangePassword = config.ChangePassword?.Supported == true,
            AuthenticationSchemes = config.AuthenticationSchemes
                .Select(s => s.Type)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList(),
            Warnings = warnings
        };
    }
}
