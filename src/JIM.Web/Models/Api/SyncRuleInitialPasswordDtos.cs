// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Synchronisation Rule's initial-password configuration, as returned by the API.
/// <para>
/// A sub-resource of the rule rather than a field on <c>SyncRuleHeader</c>: the header is a flat list
/// projection, and the query behind list endpoints does not load this navigation, so folding it in would
/// report every rule in a list as having no initial password configured. Its own endpoint loads the rule the
/// way that carries it.
/// </para>
/// <para>
/// <b>Carries no password, and never will.</b> Passwords are generated at the moment they are set and are not
/// stored, so there is nothing here to return.
/// </para>
/// </summary>
public class SyncRuleInitialPasswordResponse
{
    /// <summary>
    /// Whether JIM sets an initial password on the accounts this Synchronisation Rule provisions.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where the generator settings come from: the Connected System's discovered policy, or the settings saved
    /// on this rule.
    /// </summary>
    public InitialPasswordSource Source { get; set; }

    /// <summary>
    /// The settings used when <see cref="Source"/> is Custom. Present regardless, since they are what would be
    /// used if the source were switched.
    /// </summary>
    public PasswordGenerationPolicyDto CustomPolicy { get; set; } = new();

    /// <summary>
    /// What happens to the password once it is set.
    /// </summary>
    public PasswordExpiryBehaviour ExpiryBehaviour { get; set; }

    /// <summary>
    /// Whether the account is enabled once the password is set.
    /// </summary>
    public bool EnableAccount { get; set; }

    /// <summary>
    /// How many accounts this rule provisioned are waiting on a change to these settings, and what the target
    /// said about each. Empty in the ordinary case.
    /// <para>
    /// Reported here rather than as its own endpoint because it is the same question as "what are these settings
    /// doing": an administrator scripting a check across every rule wants the answer in the response they were
    /// already fetching, not in a second call per rule.
    /// </para>
    /// </summary>
    public List<InitialPasswordRejectionDto> ParkedReasons { get; set; } = [];

    /// <summary>
    /// The total across <see cref="ParkedReasons"/>, so a caller checking "does anything need me?" does not have
    /// to sum the list itself.
    /// </summary>
    public int ParkedAccountCount { get; set; }

    /// <summary>
    /// How many accounts this rule provisioned were never given an initial password within its time to live.
    /// <para>
    /// Kept separate from the parked count deliberately: correcting these settings releases the parked accounts
    /// and does nothing at all for the expired ones, which need a password by other means.
    /// </para>
    /// </summary>
    public int ExpiredAccountCount { get; set; }

    /// <summary>
    /// Builds the response from a Synchronisation Rule's configuration. A rule with none is reported as
    /// switched off with JIM's defaults, which is what it behaves as.
    /// </summary>
    public static SyncRuleInitialPasswordResponse FromEntity(
        SyncRuleInitialPassword? entity,
        IReadOnlyList<InitialPasswordRejection>? parkedReasons = null,
        InitialPasswordAttention? attention = null)
    {
        entity ??= new SyncRuleInitialPassword();

        return new SyncRuleInitialPasswordResponse
        {
            Enabled = entity.Enabled,
            Source = entity.Source,
            CustomPolicy = PasswordGenerationPolicyDto.FromEntity(entity.CustomPolicy),
            ExpiryBehaviour = entity.ExpiryBehaviour,
            EnableAccount = entity.EnableAccount,
            ParkedReasons = parkedReasons?.Select(InitialPasswordRejectionDto.FromEntity).ToList() ?? [],
            ParkedAccountCount = attention?.ParkedCount ?? 0,
            ExpiredAccountCount = attention?.ExpiredCount ?? 0
        };
    }
}

/// <summary>
/// One reason a target gave for refusing an initial password, and how many accounts it is holding up.
/// </summary>
public class InitialPasswordRejectionDto
{
    /// <summary>
    /// What the target said, unaltered. Null where it refused without saying anything.
    /// </summary>
    public string? TargetMessage { get; set; }

    /// <summary>
    /// How JIM classified the refusal.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// How many accounts are parked on this reason.
    /// </summary>
    public int AccountCount { get; set; }

    /// <summary>
    /// The earliest attempt that produced this reason, in UTC.
    /// </summary>
    public DateTime? FirstSeenAt { get; set; }

    public static InitialPasswordRejectionDto FromEntity(InitialPasswordRejection entity) => new()
    {
        TargetMessage = entity.TargetMessage,
        FailureReason = entity.FailureReason,
        AccountCount = entity.AccountCount,
        FirstSeenAt = entity.FirstSeenAt
    };
}

/// <summary>
/// Request DTO for replacing a Synchronisation Rule's initial-password configuration.
/// <para>
/// Every field is optional and an omitted one leaves the stored value unchanged, matching how the rule's own
/// update endpoint behaves.
/// </para>
/// </summary>
public class UpdateSyncRuleInitialPasswordRequest
{
    /// <summary>
    /// Whether JIM sets an initial password on the accounts this Synchronisation Rule provisions.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Where the generator settings come from. Discovered follows the Connected System's own policy and
    /// re-derives whenever that policy is read again; Custom uses exactly what is saved here.
    /// </summary>
    public InitialPasswordSource? Source { get; set; }

    /// <summary>
    /// The generator settings used when <c>Source</c> is Custom. Omit to leave them unchanged.
    /// </summary>
    public PasswordGenerationPolicyDto? CustomPolicy { get; set; }

    /// <summary>
    /// What happens to the password once it is set. A Connector that cannot honour the choice reports what it
    /// applied instead, per account, rather than the request being rejected here.
    /// </summary>
    public PasswordExpiryBehaviour? ExpiryBehaviour { get; set; }

    /// <summary>
    /// Whether the account is enabled once the password is set.
    /// </summary>
    public bool? EnableAccount { get; set; }

    /// <summary>
    /// An optional reason for the change, recorded against this Synchronisation Rule's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// How JIM generates an initial password, over the API.
/// </summary>
public class PasswordGenerationPolicyDto
{
    /// <summary>
    /// Random characters, words, or pronounceable syllables.
    /// </summary>
    public PasswordGenerationStyle Style { get; set; } = PasswordGenerationStyle.RandomCharacters;

    /// <summary>
    /// How many characters to produce. Ignored by the Words style, whose length follows from the words drawn.
    /// </summary>
    [Range(1, 256)]
    public int Length { get; set; } = 16;

    /// <summary>
    /// The fewest uppercase letters a generated password must contain (Random characters style).
    /// </summary>
    [Range(0, 64)]
    public int MinimumUppercase { get; set; } = 1;

    /// <summary>
    /// The fewest lowercase letters a generated password must contain (Random characters style).
    /// </summary>
    [Range(0, 64)]
    public int MinimumLowercase { get; set; } = 1;

    /// <summary>
    /// The fewest digits a generated password must contain (Random characters style).
    /// </summary>
    [Range(0, 64)]
    public int MinimumDigits { get; set; } = 1;

    /// <summary>
    /// The fewest symbols a generated password must contain (Random characters style).
    /// </summary>
    [Range(0, 64)]
    public int MinimumSymbols { get; set; } = 1;

    /// <summary>
    /// The symbols JIM may use. Narrow this where something downstream cannot cope with a given character.
    /// </summary>
    [StringLength(128)]
    public string PermittedSymbols { get; set; } = PasswordGenerationPolicy.DefaultSymbols;

    /// <summary>
    /// How many words to draw (Words style).
    /// </summary>
    [Range(1, 16)]
    public int WordCount { get; set; } = 4;

    /// <summary>
    /// What goes between the words (Words style).
    /// </summary>
    public PasswordWordSeparator WordSeparator { get; set; } = PasswordWordSeparator.Hyphen;

    /// <summary>
    /// How the words are capitalised (Words style).
    /// </summary>
    public PasswordWordCapitalisation WordCapitalisation { get; set; } = PasswordWordCapitalisation.EachWord;

    /// <summary>
    /// How many digits to append (Words and Pronounceable styles). Usually how a passphrase reaches the three
    /// character categories a stock Active Directory domain requires.
    /// </summary>
    [Range(0, 16)]
    public int AppendedDigitCount { get; set; } = 2;

    /// <summary>
    /// Whether to append one symbol (Words and Pronounceable styles).
    /// </summary>
    public bool AppendSymbol { get; set; }

    /// <summary>
    /// Whether to leave out characters that are easily confused when a password is read out or copied by hand.
    /// </summary>
    public bool ExcludeAmbiguousCharacters { get; set; } = true;

    public static PasswordGenerationPolicyDto FromEntity(PasswordGenerationPolicy entity) =>
        new()
        {
            Style = entity.Style,
            Length = entity.Length,
            MinimumUppercase = entity.MinimumUppercase,
            MinimumLowercase = entity.MinimumLowercase,
            MinimumDigits = entity.MinimumDigits,
            MinimumSymbols = entity.MinimumSymbols,
            PermittedSymbols = entity.PermittedSymbols,
            WordCount = entity.WordCount,
            WordSeparator = entity.WordSeparator,
            WordCapitalisation = entity.WordCapitalisation,
            AppendedDigitCount = entity.AppendedDigitCount,
            AppendSymbol = entity.AppendSymbol,
            ExcludeAmbiguousCharacters = entity.ExcludeAmbiguousCharacters
        };

    /// <summary>
    /// Copies these settings onto the stored policy. A whole replacement rather than a per-field merge: the
    /// generator settings only make sense as a set, and half-updating them is how a configuration ends up
    /// producing passwords nobody asked for.
    /// </summary>
    public void ApplyTo(PasswordGenerationPolicy target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Style = Style;
        target.Length = Length;
        target.MinimumUppercase = MinimumUppercase;
        target.MinimumLowercase = MinimumLowercase;
        target.MinimumDigits = MinimumDigits;
        target.MinimumSymbols = MinimumSymbols;
        target.PermittedSymbols = PermittedSymbols;
        target.WordCount = WordCount;
        target.WordSeparator = WordSeparator;
        target.WordCapitalisation = WordCapitalisation;
        target.AppendedDigitCount = AppendedDigitCount;
        target.AppendSymbol = AppendSymbol;
        target.ExcludeAmbiguousCharacters = ExcludeAmbiguousCharacters;
    }
}
