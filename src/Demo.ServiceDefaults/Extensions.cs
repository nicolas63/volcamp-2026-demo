using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using Demo.ServiceDefaults;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"]
            ?? builder.Environment.ApplicationName;
        var dashboardEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var dashboardHeaders = builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"];
        var dashboardProtocol = string.Equals(
            builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"],
            "http/protobuf",
            StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
        var lgtmEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_LGTM_ENDPOINT"];

        void ConfigureDashboard(OtlpExporterOptions options, string signal)
        {
            options.Protocol = dashboardProtocol;
            options.Endpoint = CreateSignalEndpoint(dashboardEndpoint!, dashboardProtocol, signal);
            options.Headers = dashboardHeaders;
        }

        void ConfigureLgtm(OtlpExporterOptions options, string signal)
        {
            options.Protocol = OtlpExportProtocol.HttpProtobuf;
            options.Endpoint = CreateSignalEndpoint(
                lgtmEndpoint!,
                OtlpExportProtocol.HttpProtobuf,
                signal);
        }

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            if (!string.IsNullOrWhiteSpace(dashboardEndpoint))
            {
                logging.AddOtlpExporter(
                    "aspire-dashboard-logs",
                    options => ConfigureDashboard(options, "logs"));
            }

            if (!string.IsNullOrWhiteSpace(lgtmEndpoint))
            {
                logging.AddOtlpExporter(
                    "lgtm-logs",
                    options => ConfigureLgtm(options, "logs"));
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName)
                .AddAttributes([
                    new("service.namespace", "demo-otel"),
                    new("deployment.environment.name", builder.Environment.EnvironmentName.ToLowerInvariant())
                ]))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(dashboardEndpoint))
                {
                    metrics.AddOtlpExporter(
                        "aspire-dashboard-metrics",
                        options => ConfigureDashboard(options, "metrics"));
                }

                if (!string.IsNullOrWhiteSpace(lgtmEndpoint))
                {
                    metrics.AddOtlpExporter(
                        "lgtm-metrics",
                        options => ConfigureLgtm(options, "metrics"));
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    .AddHttpClientInstrumentation();

                if (string.Equals(
                    builder.Environment.ApplicationName,
                    "DiagnosticAgent",
                    StringComparison.Ordinal))
                {
                    tracing.AddProcessor(new GenAiCostProcessor(
                        builder.Environment.ApplicationName,
                        GenAiPricing.FromConfiguration(builder.Configuration)));
                }

                if (!string.IsNullOrWhiteSpace(dashboardEndpoint))
                {
                    tracing.AddOtlpExporter(
                        "aspire-dashboard-traces",
                        options => ConfigureDashboard(options, "traces"));
                }

                if (!string.IsNullOrWhiteSpace(lgtmEndpoint))
                {
                    tracing.AddOtlpExporter(
                        "lgtm-traces",
                        options => ConfigureLgtm(options, "traces"));
                }
            });

        return builder;
    }

    private static Uri CreateSignalEndpoint(
        string endpoint,
        OtlpExportProtocol protocol,
        string signal)
    {
        var normalized = endpoint.TrimEnd('/');
        if (protocol == OtlpExportProtocol.HttpProtobuf
            && !normalized.EndsWith($"/v1/{signal}", StringComparison.OrdinalIgnoreCase))
        {
            normalized += $"/v1/{signal}";
        }

        return new Uri(normalized);
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }
}
