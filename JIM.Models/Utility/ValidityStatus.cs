namespace JIM.Models.Utility
{
    /// <summary>
    /// Describes the validity status of a JIM object.
    /// </summary>
    public class ValidityStatus
    {
        /// <summary>
        /// Denotes whether or not the object is valid, i.e. can it be persisted and/or executed.
        /// </summary>
        public bool IsValid
        {
            get
            {
                // true if there's no items with a warning or above
                return !Items.Any(q => q.Level > ValidityStatusItemLevel.Warning);
            }
        }

        public List<ValidityStatusItem> Items { get; } = new();

        public void AddItem(ValidityStatusItemLevel level, string message) 
        { 
            Items.Add(new ValidityStatusItem(level, message));
        }
    }

    public class ValidityStatusItem
    {
        public ValidityStatusItemLevel Level { get; set; }

        public string Message { get; set; }

        public ValidityStatusItem(ValidityStatusItemLevel level, string message) 
        {
            Level = level;
            Message = message;
        }
    }

    public enum ValidityStatusItemLevel
    {
        NotSet = 0,
        
        /// <summary>
        /// The object will function, and can be persisted, but it's not in a recommended state.
        /// </summary>
        Warning = 1,
        
        /// <summary>
        /// The object will not function, and cannot be persisted in its current state.
        /// </summary>
        Error = 2
    }
}
