// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A problem with the SQL Object Types document as the JSON view shows it: the parser's own message, and the
/// line to mark for it.
/// </summary>
/// <param name="Message">The message saving the document would refuse it with.</param>
/// <param name="Line">The one-based line to mark, or null where there is none (an empty document).</param>
/// <param name="StopsReading">Whether the problem is in the JSON itself (it is not JSON, or has a field the document
/// does not), so nothing can read the document until it is fixed: the cards included.</param>
public sealed record SqlObjectTypesJsonProblem(string Message, int? Line, bool StopsReading);
