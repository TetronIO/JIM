// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Logic;

/// <summary>
/// Whether, and how, a Synchronisation Rule gives a newly provisioned account its first password.
/// <para>
/// Held on the Synchronisation Rule rather than on the Connected System because rules are how JIM
/// distinguishes populations. A rule provisioning contractors and a rule provisioning permanent staff into the
/// same directory can reasonably want different password rules, and there is nowhere else to express that.
/// </para>
/// <para>
/// A row exists only for a rule somebody has configured. Its absence means no initial password, which is the
/// state every rule starts in and stays in until an administrator decides otherwise: JIM setting passwords on
/// accounts nobody asked it to is not a sensible default.
/// </para>
/// </summary>
public class SyncRuleInitialPassword
{
    public int Id { get; set; }

    public SyncRule SyncRule { get; set; } = null!;

    public int SyncRuleId { get; set; }

    /// <summary>
    /// Whether to set a password when this rule provisions an account.
    /// <para>
    /// Separate from the row's existence so an administrator can switch delivery off without losing the
    /// generator configuration they tuned, and switch it back on without rebuilding it.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the generator follows what JIM discovered on the Connected System, or a configuration the
    /// administrator wrote here.
    /// </summary>
    public InitialPasswordSource Source { get; set; } = InitialPasswordSource.Discovered;

    /// <summary>
    /// The generator configuration used when <see cref="Source"/> is
    /// <see cref="InitialPasswordSource.Custom"/>.
    /// <para>
    /// Kept even while the source is Discovered, so that switching between the two is not destructive. It is
    /// seeded from the discovered policy when the section is first configured, which means "Custom" starts from
    /// something that works rather than from nothing.
    /// </para>
    /// </summary>
    public PasswordGenerationPolicy CustomPolicy { get; set; } = new();

    /// <summary>
    /// The password to set on every account this rule provisions, encrypted at rest, used when
    /// <see cref="Source"/> is <see cref="InitialPasswordSource.Static"/>.
    /// <para>
    /// <b>This is the only password value JIM stores anywhere.</b> Every other password it handles is generated
    /// at the moment it is delivered and dropped; a static password has to survive until the next account is
    /// provisioned, so it cannot be. It is encrypted through <c>ICredentialProtectionService</c>, exactly as a
    /// Connected System's bind credential is, and is never returned by the portal, the REST API or PowerShell.
    /// Configuration change history records a keyed hash of it rather than the value.
    /// </para>
    /// <para>
    /// <b>Replaced only when a new plaintext is supplied.</b> Encryption is non-deterministic, so re-encrypting
    /// an unchanged password produces different ciphertext; a save that re-encrypted every time would read as a
    /// change to <see cref="WouldDeliverTheSameAs"/> and release the accounts parked against this rule for
    /// nothing. An empty password field on the way in therefore means "leave this as it is", which is also what
    /// lets the field be write-only without a special case anywhere.
    /// </para>
    /// </summary>
    public string? StaticPasswordEncryptedValue { get; set; }

    /// <summary>
    /// When the static password was last changed, or null where none has been set.
    /// <para>
    /// A shared password should be changed whenever somebody who knew it leaves, and nothing else in JIM can
    /// say how long the current one has been in use. Reported by every surface precisely because the password
    /// itself is not.
    /// </para>
    /// <para>
    /// Deliberately not part of <see cref="WouldDeliverTheSameAs"/>: it records when a change happened rather
    /// than being an input to what gets delivered, and it moves only when
    /// <see cref="StaticPasswordEncryptedValue"/> does, which the comparison already notices.
    /// </para>
    /// </summary>
    public DateTime? StaticPasswordSetAt { get; set; }

    /// <summary>
    /// What should happen to the password once set: whether the account holder must choose a new one at first
    /// sign-in, whether it ages normally, or whether it never expires.
    /// <para>
    /// Requiring a change at next sign-in is the default and is what makes the rest of this feature's handling
    /// of the password proportionate: the generated value is a means of getting the account holder to their own
    /// password, not a credential meant to live for long.
    /// </para>
    /// </summary>
    public PasswordExpiryBehaviour ExpiryBehaviour { get; set; } = PasswordExpiryBehaviour.RequireChangeAtNextSignIn;

    /// <summary>
    /// Whether to enable the account as part of setting its password.
    /// <para>
    /// On by default, because a provisioned account that nobody can sign in to is rarely what was wanted.
    /// Active Directory refuses to enable an account that has no policy-compliant password, so the enable has to
    /// follow the password rather than accompany the create; the Connector owns that ordering.
    /// </para>
    /// </summary>
    public bool EnableAccount { get; set; } = true;

    /// <summary>
    /// A detached copy of everything <see cref="WouldDeliverTheSameAs"/> compares, for holding what was saved
    /// while the editor mutates the live instance in place.
    /// <para>
    /// The portal needs this to tell an administrator, before they save, whether saving will release the accounts
    /// parked against this rule. It cannot answer that by comparing the instance with itself, and re-reading the
    /// saved row on every keystroke to find out would be a query per character typed.
    /// </para>
    /// <para>
    /// Only the delivery settings are copied; the identity and navigation are deliberately left off, because this
    /// is a value to compare against and never something to persist.
    /// <c>SyncRuleInitialPasswordComparisonTests</c> fails if a setting is added and not copied here, which would
    /// otherwise have the portal quietly stop offering the release for that setting.
    /// </para>
    /// </summary>
    public SyncRuleInitialPassword SnapshotDeliverySettings()
    {
        return new SyncRuleInitialPassword
        {
            Enabled = Enabled,
            Source = Source,
            ExpiryBehaviour = ExpiryBehaviour,
            EnableAccount = EnableAccount,
            StaticPasswordEncryptedValue = StaticPasswordEncryptedValue,
            CustomPolicy = new PasswordGenerationPolicy
            {
                Style = CustomPolicy.Style,
                Length = CustomPolicy.Length,
                MinimumUppercase = CustomPolicy.MinimumUppercase,
                MinimumLowercase = CustomPolicy.MinimumLowercase,
                MinimumDigits = CustomPolicy.MinimumDigits,
                MinimumSymbols = CustomPolicy.MinimumSymbols,
                PermittedSymbols = CustomPolicy.PermittedSymbols,
                WordCount = CustomPolicy.WordCount,
                WordSeparator = CustomPolicy.WordSeparator,
                WordCapitalisation = CustomPolicy.WordCapitalisation,
                AppendedDigitCount = CustomPolicy.AppendedDigitCount,
                AppendSymbol = CustomPolicy.AppendSymbol,
                ExcludeAmbiguousCharacters = CustomPolicy.ExcludeAmbiguousCharacters
            }
        };
    }

    /// <summary>
    /// Whether two configurations would produce the same delivery: the same password, applied the same way.
    /// <para>
    /// This decides whether saving a Synchronisation Rule releases the accounts parked against it. Parking stops
    /// the retry loop because the target refused the password these settings produce, so only a change to the
    /// settings makes another attempt worth making. Saving an unrelated part of the rule must not set those
    /// accounts retrying against a configuration the target has already given its answer on: the retry would
    /// fail identically and inflate an attempt count that is supposed to mean "distinct configurations tried".
    /// </para>
    /// <para>
    /// A null on either side is a real state, not a missing value; it means the rule does not set initial
    /// passwords. Switching that on or off is itself a change of delivery.
    /// </para>
    /// <para>
    /// Every setting that reaches the generator or the Connector is compared, and
    /// <c>SyncRuleInitialPasswordComparisonCompletenessTests</c> fails if a property is added to this class or
    /// to <see cref="PasswordGenerationPolicy"/> without being accounted for here. Without that guard a new
    /// setting would silently stop releasing parked work, which is the failure this whole comparison exists to
    /// prevent.
    /// </para>
    /// </summary>
    public static bool WouldDeliverTheSameAs(SyncRuleInitialPassword? left, SyncRuleInitialPassword? right)
    {
        if (left == null || right == null)
            return left == null && right == null;

        if (left.Enabled != right.Enabled || left.Source != right.Source ||
            left.ExpiryBehaviour != right.ExpiryBehaviour || left.EnableAccount != right.EnableAccount)
            return false;

        // Ciphertext, compared as an opaque string. That is sound only because the stored value is replaced
        // exclusively when a new plaintext is supplied: encryption is non-deterministic, so re-encrypting an
        // unchanged password on every save would make each one look like a change and release the parked
        // accounts for nothing. Compared whatever the Source says, for the same reason the policy below is.
        if (!string.Equals(left.StaticPasswordEncryptedValue, right.StaticPasswordEncryptedValue, StringComparison.Ordinal))
            return false;

        // Compared whatever the Source says, deliberately. An administrator can correct the custom settings
        // while the rule is on Discovered and switch over afterwards, and the switch alone would then look like
        // the only change; comparing both ways round means neither ordering loses the release.
        var a = left.CustomPolicy;
        var b = right.CustomPolicy;

        return a.Style == b.Style &&
               a.Length == b.Length &&
               a.MinimumUppercase == b.MinimumUppercase &&
               a.MinimumLowercase == b.MinimumLowercase &&
               a.MinimumDigits == b.MinimumDigits &&
               a.MinimumSymbols == b.MinimumSymbols &&
               a.PermittedSymbols == b.PermittedSymbols &&
               a.WordCount == b.WordCount &&
               a.WordSeparator == b.WordSeparator &&
               a.WordCapitalisation == b.WordCapitalisation &&
               a.AppendedDigitCount == b.AppendedDigitCount &&
               a.AppendSymbol == b.AppendSymbol &&
               a.ExcludeAmbiguousCharacters == b.ExcludeAmbiguousCharacters;
    }
}
