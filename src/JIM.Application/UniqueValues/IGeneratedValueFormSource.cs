// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// Works out a generation request's candidates' forms (Generated Value Forms, #2023), so
/// <see cref="UniqueValueGenerationServer"/> can check them with the candidate while staying free of Synchronisation
/// Rules: the caller, which holds the object and its rules, supplies one per request that has forms
/// (<see cref="GenerationRequest.FormSource"/>).
/// </summary>
public interface IGeneratedValueFormSource
{
    /// <summary>
    /// The forms of <paramref name="candidate"/> (with <paramref name="numericCandidate"/> for a Number target). Answers
    /// the same candidate the same way every time it is asked, so the probe window can look ahead.
    /// </summary>
    /// <exception cref="JIM.Models.Sync.SyncExpressionEvaluationException">A form's expression failed: the candidate is
    /// not issued, and the failure is recorded as any derived flow's is (PRD requirement 6).</exception>
    public GeneratedValueFormEvaluation Evaluate(string candidate, long? numericCandidate);
}
