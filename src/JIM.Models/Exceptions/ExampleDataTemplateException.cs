// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

public class ExampleDataTemplateException : OperationalException
{
    public ExampleDataTemplateException(string message) : base(message)
    {
    }

    public ExampleDataTemplateException(string message, Exception inner) : base(message, inner)
    {
    }
}