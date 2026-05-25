namespace CaravanCMS.Api.Middleware;

/// <summary>
/// Validates the X-API-Key header on every request except Swagger UI and health endpoints.
/// Returns 401 Unauthorized if the key is missing or incorrect.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string ConfigKey = "CaravanCMS:ApiKey";

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly string _expectedApiKey;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, IConfiguration config)
    {
        _next = next;
        _logger = logger;
        _expectedApiKey = config[ConfigKey]
            ?? throw new InvalidOperationException($"Missing required configuration: {ConfigKey}");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;

        // Skip auth for Swagger and health — these are non-sensitive
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out Microsoft.Extensions.Primitives.StringValues extractedKey))
        {
            _logger.LogWarning("Request to {Path} rejected — missing {Header}", path, ApiKeyHeader);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API key required. Include X-API-Key header." });
            return;
        }

        if (!string.Equals(extractedKey, _expectedApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Request to {Path} rejected — invalid API key supplied", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key." });
            return;
        }

        await _next(context);
    }
}
