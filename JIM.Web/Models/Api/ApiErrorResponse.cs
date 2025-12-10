namespace JIM.Web.Models.Api;

/// <summary>
/// Standardised error response for API errors.
/// </summary>
public class ApiErrorResponse
{
    /// <summary>
    /// A machine-readable error code.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// A human-readable error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional detailed information about the error.
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Validation errors keyed by field name.
    /// </summary>
    public Dictionary<string, string[]>? ValidationErrors { get; set; }

    /// <summary>
    /// UTC timestamp when the error occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a validation error response.
    /// </summary>
    public static ApiErrorResponse ValidationError(string message, Dictionary<string, string[]>? errors = null)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.ValidationError,
            Message = message,
            ValidationErrors = errors
        };
    }

    /// <summary>
    /// Creates a not found error response.
    /// </summary>
    public static ApiErrorResponse NotFound(string message)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.NotFound,
            Message = message
        };
    }

    /// <summary>
    /// Creates an unauthorised error response.
    /// </summary>
    public static ApiErrorResponse Unauthorised(string message)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.Unauthorised,
            Message = message
        };
    }

    /// <summary>
    /// Creates a forbidden error response.
    /// </summary>
    public static ApiErrorResponse Forbidden(string message)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.Forbidden,
            Message = message
        };
    }

    /// <summary>
    /// Creates a conflict error response.
    /// </summary>
    public static ApiErrorResponse Conflict(string message)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.Conflict,
            Message = message
        };
    }

    /// <summary>
    /// Creates an internal server error response.
    /// </summary>
    public static ApiErrorResponse InternalError(string message, string? details = null)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.InternalError,
            Message = message,
            Details = details
        };
    }

    /// <summary>
    /// Creates a bad request error response.
    /// </summary>
    public static ApiErrorResponse BadRequest(string message)
    {
        return new ApiErrorResponse
        {
            Code = ApiErrorCodes.BadRequest,
            Message = message
        };
    }
}

/// <summary>
/// Standard API error codes.
/// </summary>
public static class ApiErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorised = "UNAUTHORISED";
    public const string Forbidden = "FORBIDDEN";
    public const string Conflict = "CONFLICT";
    public const string InternalError = "INTERNAL_ERROR";
    public const string BadRequest = "BAD_REQUEST";
}
