using System.Net;
using System.Text.Json;
using JIM.Web.Models.Api;

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
        _logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

        var response = context.Response;
        response.ContentType = "application/json";

        var errorResponse = exception switch
        {
            KeyNotFoundException => new ApiErrorResponse
            {
                Code = ApiErrorCodes.NotFound,
                Message = exception.Message
            },
            UnauthorizedAccessException => new ApiErrorResponse
            {
                Code = ApiErrorCodes.Unauthorised,
                Message = exception.Message
            },
            InvalidOperationException => new ApiErrorResponse
            {
                Code = ApiErrorCodes.BadRequest,
                Message = exception.Message
            },
            ArgumentException => new ApiErrorResponse
            {
                Code = ApiErrorCodes.ValidationError,
                Message = exception.Message
            },
            _ => new ApiErrorResponse
            {
                Code = ApiErrorCodes.InternalError,
                Message = "An unexpected error occurred.",
                Details = context.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true
                    ? exception.Message
                    : null
            }
        };

        response.StatusCode = exception switch
        {
            KeyNotFoundException => (int)HttpStatusCode.NotFound,
            UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,
            InvalidOperationException => (int)HttpStatusCode.BadRequest,
            ArgumentException => (int)HttpStatusCode.BadRequest,
            _ => (int)HttpStatusCode.InternalServerError
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await response.WriteAsync(JsonSerializer.Serialize(errorResponse, options));
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
