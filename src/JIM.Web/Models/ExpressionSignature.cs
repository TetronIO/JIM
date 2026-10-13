// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;

namespace JIM.Web.Models;

/// <summary>
/// The function call the caret is inside, for the expression editor's signature help (#2053).
/// </summary>
/// <param name="Function">The function being called.</param>
/// <param name="ParameterIndex">The parameter the caret is on, counting from 0; the last one once past it.</param>
public sealed record ExpressionSignature(ExpressionFunction Function, int ParameterIndex);
