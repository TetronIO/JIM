// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions
{
    public class ExampleDataTemplateAttributeException : OperationalException
    {
        public ExampleDataTemplateAttributeException(string message) : base(message)
        {
        }

        public ExampleDataTemplateAttributeException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
