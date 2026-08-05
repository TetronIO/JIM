// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// The password policy JIM read from a Connected System itself, as returned by the API.
/// <para>
/// Everything here is nullable because a directory withholds what a caller may not see by omitting it rather
/// than refusing. A null is therefore "JIM could not read this", never "there is no such rule", and a caller
/// deciding whether a password will be accepted has to treat the two differently.
/// </para>
/// </summary>
public class ConnectedSystemPasswordPolicyResponse
{
    /// <summary>
    /// When JIM last read this from the Connected System. Null when it has never been read, which is also when
    /// every other field is null.
    /// </summary>
    public DateTime? Discovered { get; set; }

    /// <summary>
    /// The shortest password the system will accept.
    /// </summary>
    public int? MinimumLength { get; set; }

    /// <summary>
    /// Whether the system enforces a complexity rule at all.
    /// </summary>
    public bool? ComplexityRequired { get; set; }

    /// <summary>
    /// How many character categories a password must draw on.
    /// </summary>
    public int? RequiredCharacterClassCount { get; set; }

    /// <summary>
    /// The categories this system counts towards its complexity rule. A category a system does not recognise
    /// cannot help satisfy it, which is what catches the passphrase trap.
    /// </summary>
    public List<string> RecognisedCharacterClasses { get; set; } = [];

    /// <summary>
    /// How many previous passwords the system remembers and refuses.
    /// </summary>
    public int? PasswordHistoryLength { get; set; }

    /// <summary>
    /// How long a password may live, in days.
    /// </summary>
    public int? MaximumPasswordAgeDays { get; set; }

    /// <summary>
    /// How soon a password may be changed again, in days.
    /// </summary>
    public int? MinimumPasswordAgeDays { get; set; }

    /// <summary>
    /// Whether the domain has password policies that apply to only some accounts. Where these are present, or
    /// where JIM was not permitted to find out, the figures above are a floor rather than a guarantee.
    /// </summary>
    public string FineGrainedPolicySignal { get; set; } = string.Empty;

    /// <summary>
    /// Whether JIM discovered anything at all. False means the figures above say nothing about what this system
    /// will accept, rather than that it accepts anything.
    /// </summary>
    public bool HasAnyDiscoveredConstraint { get; set; }

    /// <summary>
    /// Builds the response. A system with nothing discovered is reported as such rather than as an error: a
    /// directory that will not disclose its policy is an ordinary state, not a failure.
    /// </summary>
    public static ConnectedSystemPasswordPolicyResponse FromEntity(ConnectedSystemPasswordPolicy? entity)
    {
        if (entity == null)
            return new ConnectedSystemPasswordPolicyResponse
            {
                FineGrainedPolicySignal = JIM.Models.Staging.FineGrainedPolicySignal.CouldNotDetermine.ToString()
            };

        return new ConnectedSystemPasswordPolicyResponse
        {
            Discovered = entity.Discovered,
            MinimumLength = entity.MinimumLength,
            ComplexityRequired = entity.ComplexityRequired,
            RequiredCharacterClassCount = entity.RequiredCharacterClassCount,
            RecognisedCharacterClasses = DescribeClasses(entity.RecognisedCharacterClasses),
            PasswordHistoryLength = entity.PasswordHistoryLength,
            MaximumPasswordAgeDays = entity.MaximumPasswordAge.HasValue ? (int)entity.MaximumPasswordAge.Value.TotalDays : null,
            MinimumPasswordAgeDays = entity.MinimumPasswordAge.HasValue ? (int)entity.MinimumPasswordAge.Value.TotalDays : null,
            FineGrainedPolicySignal = entity.FineGrainedPolicySignal.ToString(),
            HasAnyDiscoveredConstraint = entity.HasAnyDiscoveredConstraint
        };
    }

    /// <summary>
    /// Names the flags rather than returning the combined integer, which a caller in another language would
    /// have to know JIM's bit values to read.
    /// </summary>
    internal static List<string> DescribeClasses(PasswordCharacterClasses classes)
    {
        return Enum.GetValues<PasswordCharacterClasses>()
            .Where(c => c != PasswordCharacterClasses.None && classes.HasFlag(c))
            .Select(c => c.ToString())
            .ToList();
    }
}

/// <summary>
/// A password JIM generated at the caller's request, with what it can say about it.
/// <para>
/// <b>This is the only response body in JIM that carries a password.</b> That is deliberate and is not in
/// tension with the rest of the feature: what JIM never does is store a password, or return one nobody asked
/// for. Here the caller asked for one and is the only party that can use it, so withholding it would make the
/// call pointless. Nothing is written down: the value exists in this response and nowhere else.
/// </para>
/// </summary>
public class GeneratedPasswordResponse
{
    /// <summary>
    /// The generated password. Treat it as a credential: it is not recoverable once this response is gone.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The shortest password these settings can produce, which for most styles is also this one's length.
    /// </summary>
    public int GuaranteedMinimumLength { get; set; }

    /// <summary>
    /// How many character categories every password from these settings is guaranteed to carry.
    /// </summary>
    public int GuaranteedCharacterClassCount { get; set; }

    /// <summary>
    /// The categories it is guaranteed to carry, named.
    /// </summary>
    public List<string> GuaranteedCharacterClasses { get; set; } = [];

    /// <summary>
    /// A measure of how hard the password is to guess, in bits.
    /// </summary>
    public double EntropyBits { get; set; }

    /// <summary>
    /// True only where JIM has a discovered policy to check against and the password satisfies it.
    /// <para>
    /// False where there is no policy to check, which is not the same as failing one; the accompanying
    /// <see cref="Problems"/> list is empty in that case. A caller wanting to know which it is should read the
    /// discovered policy endpoint.
    /// </para>
    /// </summary>
    public bool SatisfiesDiscoveredPolicy { get; set; }

    /// <summary>
    /// What the target would object to, where JIM can tell. Empty where there is nothing to object to, and also
    /// where there is no discovered policy to object with.
    /// </summary>
    public List<string> Problems { get; set; } = [];

    /// <summary>
    /// Where several systems were asked for at once: those JIM could read no policy from.
    /// <para>
    /// Named rather than passed over, because the caller is about to set this password on those systems and JIM
    /// cannot promise it will be accepted there. Empty for a single-system generate.
    /// </para>
    /// </summary>
    public List<string> SystemsWithNoDiscoveredPolicy { get; set; } = [];

    /// <summary>
    /// Where several systems were asked for at once: the rules the password had to satisfy, in the words the
    /// portal uses, so a script can report what it generated against.
    /// </summary>
    public List<string> Constraints { get; set; } = [];

    /// <summary>
    /// Builds the response for several systems at once, from the reconciled policy their rules produced.
    /// </summary>
    public static GeneratedPasswordResponse FromReconciled(
        string password,
        PasswordGenerationAssessment assessment,
        PasswordPolicyReconciliation reconciliation)
    {
        var response = FromGenerated(password, assessment, reconciliation.Constraints.Count > 0);
        response.SystemsWithNoDiscoveredPolicy = [.. reconciliation.SystemsWithNoDiscoveredPolicy];
        response.Constraints = [.. reconciliation.Constraints];

        // A system that disclosed nothing may be stricter than the reconciled policy knows, so JIM must not
        // claim the password satisfies what it cannot see.
        if (reconciliation.SystemsWithNoDiscoveredPolicy.Count > 0 || reconciliation.MayBeStricterThanDiscovered)
            response.SatisfiesDiscoveredPolicy = false;

        return response;
    }

    public static GeneratedPasswordResponse FromGenerated(
        string password,
        PasswordGenerationAssessment assessment,
        bool hasDiscoveredPolicy) => new()
    {
        Password = password,
        GuaranteedMinimumLength = assessment.GuaranteedMinimumLength,
        GuaranteedCharacterClassCount = assessment.GuaranteedCharacterClassCount,
        GuaranteedCharacterClasses = ConnectedSystemPasswordPolicyResponse.DescribeClasses(assessment.GuaranteedCharacterClasses),
        EntropyBits = Math.Round(assessment.EntropyBits, 1),
        // Never true without a policy to have satisfied. Reporting compliance JIM cannot verify would be worse
        // than reporting nothing, because a caller would stop checking.
        SatisfiesDiscoveredPolicy = hasDiscoveredPolicy && assessment.IsUsable,
        Problems = [.. assessment.Problems]
    };
}

/// <summary>
/// Asks for one password that every named Connected System will accept.
/// <para>
/// This is the case that most needs JIM to generate rather than the caller: setting one password across a
/// person's accounts means satisfying the strictest of several systems at once, and an administrator cannot
/// see those policies to reason about them. JIM can.
/// </para>
/// </summary>
public class GeneratePasswordForSystemsRequest
{
    /// <summary>
    /// The Connected Systems the password has to work on. At least one is required; JIM reconciles their
    /// discovered policies into one set of rules and generates against that.
    /// </summary>
    public List<int> ConnectedSystemIds { get; set; } = [];
}
