# Security and privacy

## Trust boundaries

The LLM is untrusted. It can select only tools published by ObservabilityMcp. The MCP
server independently validates every input and constructs all backend queries.

## MCP controls

- no shell, file, restart, deployment or fault-injection tool;
- no arbitrary PromQL, LogQL, TraceQL or URL;
- service allowlist;
- 1–15 minute query windows;
- trace result limit 10;
- log result limit 50;
- 5-second backend HTTP timeouts;
- validated lowercase W3C trace IDs;
- explicit backend failures rather than empty success-shaped fallbacks.

## Secrets

Azure values are read from `src/DiagnosticAgent/appsettings.Local.json`, which is
ignored by Git and loaded only by `DiagnosticAgent`. Start from the versioned
`appsettings.Local.example.json` template. Do not place the key in `appsettings.json`,
`appsettings.Development.json`, `launchSettings.json`, replay files, screenshots or
telemetry. Use a dedicated, restricted demo resource and rotate the key after the
conference.

## Sensitive AI content

Agent Framework OpenTelemetry is configured with `EnableSensitiveData=false`.
Telemetry stores question length, model/deployment, token counts, duration, status and
diagnosis length, not prompt or response content.

## Local-only warning

LGTM is a demo distribution, not a production-hardened deployment. Bind or expose
these services only on a trusted conference workstation.
