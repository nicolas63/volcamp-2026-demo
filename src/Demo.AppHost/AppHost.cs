var builder = DistributedApplication.CreateBuilder(args);

var lgtm = builder
    .AddContainer("lgtm", "grafana/otel-lgtm", "0.33.0")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "grafana")
    .WithHttpEndpoint(port: 9090, targetPort: 9090, name: "prometheus")
    .WithHttpEndpoint(port: 3100, targetPort: 3100, name: "loki")
    .WithHttpEndpoint(port: 3200, targetPort: 3200, name: "tempo")
    .WithHttpEndpoint(port: 14318, targetPort: 4318, name: "otlp-http");

var inventory = builder
    .AddProject<Projects.InventoryService>("inventory-service", launchProfileName: "http")
    .WithEnvironment("OTEL_SERVICE_NAME", "inventory-service")
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "1000")
    .WithEnvironment("OTEL_EXPORTER_OTLP_LGTM_ENDPOINT", lgtm.GetEndpoint("otlp-http"))
    .WaitFor(lgtm);

var order = builder
    .AddProject<Projects.OrderService>("order-service", launchProfileName: "http")
    .WithEnvironment("OTEL_SERVICE_NAME", "order-service")
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "1000")
    .WithEnvironment("Inventory__BaseUrl", inventory.GetEndpoint("http"))
    .WithEnvironment("OTEL_EXPORTER_OTLP_LGTM_ENDPOINT", lgtm.GetEndpoint("otlp-http"))
    .WaitFor(inventory)
    .WaitFor(lgtm);

var mcp = builder
    .AddProject<Projects.ObservabilityMcp>("observability-mcp", launchProfileName: "http")
    .WithEnvironment("OTEL_SERVICE_NAME", "observability-mcp")
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "1000")
    .WithEnvironment("Observability__PrometheusBaseUrl", lgtm.GetEndpoint("prometheus"))
    .WithEnvironment("Observability__TempoBaseUrl", lgtm.GetEndpoint("tempo"))
    .WithEnvironment("Observability__LokiBaseUrl", lgtm.GetEndpoint("loki"))
    .WithEnvironment("OTEL_EXPORTER_OTLP_LGTM_ENDPOINT", lgtm.GetEndpoint("otlp-http"))
    .WaitFor(lgtm);

var agent = builder
    .AddProject<Projects.DiagnosticAgent>("diagnostic-agent", launchProfileName: "http")
    .WithEnvironment("OTEL_SERVICE_NAME", "diagnostic-agent")
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "1000")
    .WithEnvironment("MCP_ENDPOINT", mcp.GetEndpoint("http"))
    .WithEnvironment("AgentGuard__GlobalBudget__PrometheusBaseUrl", lgtm.GetEndpoint("prometheus"))
    .WithEnvironment("OTEL_EXPORTER_OTLP_LGTM_ENDPOINT", lgtm.GetEndpoint("otlp-http"))
    .WaitFor(mcp);

builder.Build().Run();
