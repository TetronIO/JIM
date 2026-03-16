using System.Net;
using System.Text.Json;
using JIM.Web.Models.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Middleware that catches unhandled exceptions and returns a standardised error response.
/// </summary>
public class GlobalExceptionHandler(RequestDelegate next, ILogger<GlobalExceptionHandler> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<GlobalExceptionHandler> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var isDevelopment = context.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true;

        // Check for transient database errors first — these must be handled before
        // the generic InvalidOperationException case, since EF Core wraps transient
        // Npgsql errors in InvalidOperationException.
        var isTransient = IsTransientDatabaseException(exception);

        if (isTransient)
        {
            _logger.LogWarning(exception, "Transient database error on {Method} {Path}: {Message}",
                context.Request.Method, context.Request.Path, exception.Message);
        }
        else
        {
            _logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);
        }

        var response = context.Response;
        response.ContentType = "application/json";

        ApiErrorResponse errorResponse;
        int statusCode;

        if (isTransient)
        {
            statusCode = (int)HttpStatusCode.ServiceUnavailable;
            errorResponse = new ApiErrorResponse
            {
                Code = ApiErrorCodes.ServiceUnavailable,
                Message = "The service is temporarily unavailable due to database connectivity. Please retry your request.",
                Details = isDevelopment ? exception.Message : null
            };
            response.Headers["Retry-After"] = "5";
        }
        else
        {
            (errorResponse, statusCode) = exception switch
            {
                KeyNotFoundException => (new ApiErrorResponse
                {
                    Code = ApiErrorCodes.NotFound,
                    Message = exception.Message
                }, (int)HttpStatusCode.NotFound),

                UnauthorizedAccessException => (new ApiErrorResponse
                {
                    Code = ApiErrorCodes.Unauthorised,
                    Message = exception.Message
                }, (int)HttpStatusCode.Unauthorized),

                InvalidOperationException => (new ApiErrorResponse
                {
                    Code = ApiErrorCodes.BadRequest,
                    Message = exception.Message
                }, (int)HttpStatusCode.BadRequest),

                ArgumentException => (new ApiErrorResponse
                {
                    Code = ApiErrorCodes.ValidationError,
                    Message = exception.Message
                }, (int)HttpStatusCode.BadRequest),

                _ => (new ApiErrorResponse
                {
                    Code = ApiErrorCodes.InternalError,
                    Message = "An unexpected error occurred.",
                    Details = isDevelopment ? exception.Message : null
                }, (int)HttpStatusCode.InternalServerError)
            };
        }

        response.StatusCode = statusCode;

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await response.WriteAsync(JsonSerializer.Serialize(errorResponse, options));
    }

    /// <summary>
    /// Determines whether an exception represents a transient database failure that
    /// should be reported as HTTP 503 (Service Unavailable) rather than a client error.
    /// </summary>
    /// <remarks>
    /// EF Core and Npgsql can wrap transient database errors in several ways:
    /// - <see cref="DbUpdateException"/> with a transient <see cref="NpgsqlException"/> inner exception
    /// - <see cref="InvalidOperationException"/> wrapping a transient <see cref="NpgsqlException"/>
    ///   (e.g. "An exception has been raised that is likely due to a transient failure")
    /// - Direct <see cref="NpgsqlException"/> with <see cref="NpgsqlException.IsTransient"/> = true
    /// </remarks>
    private static bool IsTransientDatabaseException(Exception exception)
    {
        // Direct Npgsql transient exception
        if (exception is NpgsqlException { IsTransient: true })
            return true;

        // EF Core DbUpdateException wrapping a transient Npgsql exception
        if (exception is DbUpdateException && HasTransientNpgsqlInner(exception))
            return true;

        // InvalidOperationException wrapping a transient Npgsql exception
        // (EF Core's connection pool exhaustion, transient connection failures)
        if (exception is InvalidOperationException && HasTransientNpgsqlInner(exception))
            return true;

        // Check for the specific EF Core transient failure message pattern
        if (exception is InvalidOperationException &&
            exception.Message.Contains("transient failure", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Walks the inner exception chain looking for a transient <see cref="NpgsqlException"/>.
    /// </summary>
    private static bool HasTransientNpgsqlInner(Exception exception)
    {
        var inner = exception.InnerException;
        while (inner != null)
        {
            if (inner is NpgsqlException { IsTransient: true })
                return true;
            inner = inner.InnerException;
        }
        return false;
    }
}

/// <summary>
/// Extension methods for registering the global exception handler middleware.
/// </summary>
public static class GlobalExceptionHandlerExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GlobalExceptionHandler>();
    }
}
