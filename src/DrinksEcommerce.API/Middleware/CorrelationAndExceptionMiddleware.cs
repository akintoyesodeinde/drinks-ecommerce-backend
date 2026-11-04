using System.Diagnostics;
using DrinksEcommerce.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace DrinksEcommerce.API.Middleware;

public sealed class CorrelationAndExceptionMiddleware(
    RequestDelegate next,
    ILogger<CorrelationAndExceptionMiddleware> logger)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 64)
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.Items[CorrelationHeader] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            try
            {
                await next(context);
            }
            catch (DomainException dex)
            {
                await WriteProblemDetailsAsync(
                    context,
                    dex.HttpStatus,
                    GetTitleForStatus(dex.HttpStatus),
                    dex.Message,
                    dex.Code,
                    correlationId);
            }
            catch (ValidationException vex)
            {
                var dict = vex.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray());

                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Validation failed",
                    "One or more validation errors occurred.",
                    "validation_error",
                    correlationId,
                    dict);
            }
            catch (BadHttpRequestException brex)
            {
                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid request body",
                    brex.Message,
                    "validation_error",
                    correlationId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "An unexpected server error occurred",
                    "An unexpected error occurred while processing your request.",
                    "internal_error",
                    correlationId);
            }
        }
    }

    private static async Task WriteProblemDetailsAsync(
        HttpContext context,
        int status,
        string title,
        string detail,
        string code,
        string correlationId,
        IDictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        var payload = new Dictionary<string, object?>
        {
            ["type"] = $"https://httpstatuses.io/{status}",
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["code"] = code,
            ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier,
            ["correlationId"] = correlationId
        };

        if (errors is not null)
        {
            payload["errors"] = errors;
        }

        await context.Response.WriteAsJsonAsync(payload);
    }

    private static string GetTitleForStatus(int status) => status switch
    {
        400 => "Validation failed",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Resource not found",
        409 => "Conflict",
        422 => "Business rule violation",
        429 => "Too many requests",
        _ => "Request failed"
    };
}
