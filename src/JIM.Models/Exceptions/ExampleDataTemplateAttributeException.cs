// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Exceptions
{
    public class ExampleDataTemplateAttributeException : OperationalException, IJimMessageException
    {
        /// <summary>The refusal, for rendering in the reader's language; null where it was built from English text.</summary>
        public JimMessage? Detail { get; }

        public ExampleDataTemplateAttributeException(JimMessage detail) : base(detail.ToEnglish())
        {
            Detail = detail;
        }

        public ExampleDataTemplateAttributeException(string message) : base(message)
        {
        }

        public ExampleDataTemplateAttributeException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
