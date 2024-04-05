using JIM.Models.Utility;

namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Enableds a JIM object to have it's state evaluated for validity, i.e. to determine whether or not it can be persisted, and/or executed in its current state.
    /// To be managed by Servers in JIM.Application.
    /// </summary>
    public interface IValidated
    {
        /// <summary>
        /// Returns a simple yes/no for if the object is valid. Commonly use for enabling/disabling form submission.
        /// </summary>
        public bool IsValid();

        /// <summary>
        /// Returns a long-form version of the response from IsValid(), with messages to help the user understand why it's not valid.
        /// </summary>
        public List<ValidityStatusItem> Validate();
    }
}
