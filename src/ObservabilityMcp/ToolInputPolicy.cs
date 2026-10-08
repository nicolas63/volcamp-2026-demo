using System.Text.RegularExpressions;

namespace ObservabilityMcp;

public static partial class ToolInputPolicy
{
    private static readonly HashSet<string> AllowedServices =
    [
        "order-service",
        "inventory-service",
        "diagnostic-agent",
        "observability-mcp"
    ];

    public static string Service(string serviceName)
    {
        if (!AllowedServices.Contains(serviceName))
        {
            throw new ArgumentOutOfRangeException(
                nameof(serviceName),
                serviceName,
                "Service is not in the observability allowlist.");
        }

        return serviceName;
    }

    public static int Window(int windowMinutes)
    {
        if (windowMinutes is < 1 or > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowMinutes),
                "Window must be between 1 and 15 minutes.");
        }

        return windowMinutes;
    }

    public static int Limit(int limit, int maximum)
    {
        if (limit < 1 || limit > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Limit must be between 1 and {maximum}.");
        }

        return limit;
    }

    public static string DependencyServerPort(string callerService, string dependencyService)
    {
        Service(callerService);
        Service(dependencyService);

        return (callerService, dependencyService) switch
        {
            ("order-service", "inventory-service") => "5102",
            _ => throw new ArgumentOutOfRangeException(
                nameof(dependencyService),
                $"{callerService} to {dependencyService} is not an allowlisted dependency edge.")
        };
    }

    public static string TraceId(string traceId)
    {
        if (!TraceIdPattern().IsMatch(traceId))
        {
            throw new ArgumentException(
                "Trace ID must contain exactly 32 lowercase hexadecimal characters.",
                nameof(traceId));
        }

        return traceId;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdPattern();
}
