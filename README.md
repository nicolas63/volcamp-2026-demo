# OpenTelemetry + AI agents conference demo

Local .NET 10 demo showing that the same OpenTelemetry data can be used by:

1. a human investigating `OrderService` and `InventoryService` in Aspire Dashboard;
2. a Microsoft Agent Framework agent querying Prometheus, Tempo and Loki through
   bounded, read-only MCP tools;
3. a human auditing the agent itself through GenAI spans, tool calls, durations,
   retries and token usage.

The demo uses an Aspire AppHost. AppHost launches the four .NET projects and runs
Grafana OTEL-LGTM as a Docker container. Kubernetes is not used.

## Prerequisites

- .NET SDK 10
- Docker
- PowerShell 7 recommended
- Optional for live AI diagnosis: Azure OpenAI deployment supporting tool calls and
  its API key

## Start the demo

For live diagnosis, create the ignored local settings file once:

```powershell
Copy-Item `
  .\src\DiagnosticAgent\appsettings.Local.example.json `
  .\src\DiagnosticAgent\appsettings.Local.json
```

Then edit `src\DiagnosticAgent\appsettings.Local.json` with the endpoint, deployment
and API key. The local file is ignored by Git and loaded only by DiagnosticAgent.
Do not put the key in the versioned settings files or presentation material.

Start the complete environment:

```powershell
.\scripts\start-demo.ps1
```

The console prints the Aspire Dashboard URL and access token. AppHost starts the four
.NET resources plus the pinned `grafana/otel-lgtm:0.33.0` image through Docker.

## Run the demo

```powershell
.\scripts\warmup-demo.ps1
.\scripts\run-healthy.ps1
.\scripts\run-failure.ps1
.\scripts\run-agent.ps1
```

If Azure OpenAI is unavailable:

```powershell
.\scripts\run-replay.ps1
```

Replay telemetry is always tagged `demo.mode=replay`.

`run-agent.ps1` also prints an estimated token cost. Its defaults correspond to the
GPT-4.1 mini Global rates used by the demo at the time of writing. Override the three
rates when the Azure deployment uses Data Zone, priority processing or updated prices:

```powershell
.\scripts\run-agent.ps1 `
  -InputUsdPerMillion 0.40 `
  -CachedInputUsdPerMillion 0.10 `
  -OutputUsdPerMillion 1.60
```

Reasoning tokens are already included in output tokens and must not be billed twice.
Replay tokens are synthetic, so replay always reports a real Azure cost of zero.

### Agent guard

Every live run is bounded by a guard: 90 s, 60 000 tokens, 0.05 USD, 8 tool calls, plus
a global budget of 0.50 USD per sliding hour, read from Prometheus. To show the guard
triggering on stage:

```powershell
.\scripts\run-agent-guard-strict.ps1   # same as run-agent.ps1 -GuardProfile Strict (8 000 tokens, 2 tool calls)
```

The agent returns a partial French report. Its `invoke_agent` span is marked as an error
and carries a `guard.triggered` event and the `demo.agent.guard.*` attributes. The metric
`demo_agent_guard_triggers_total` is incremented. If the global budget is exceeded, the API
returns HTTP 429 and the agent is not started. Limits are configured in the `AgentGuard` section
(see `docs/observability.md`).

## Endpoints

| Component | URL |
|---|---|
| OrderService | <http://localhost:5101> |
| InventoryService | <http://localhost:5102> |
| Observability MCP | <http://localhost:5103/mcp> |
| DiagnosticAgent | <http://localhost:5104> |
| Grafana | <http://localhost:3000> |
| Prometheus | <http://localhost:9090> |
| Loki | <http://localhost:3100> |
| Tempo | <http://localhost:3200> |

## Expected failure

`VOLCAMP-63` causes a deterministic 2500 ms inventory delay. OrderService applies a
700 ms timeout per attempt and retries twice after 150 ms and 300 ms. The request
therefore returns HTTP 504 after exactly three dependency attempts.

## Validate

```powershell
dotnet test DemoOtel.slnx
.\scripts\verify-demo.ps1
```

## Documentation

- [Architecture and flows](docs/architecture.md)
- [OpenTelemetry signals](docs/observability.md)
- [Security and privacy](docs/security-and-privacy.md)
