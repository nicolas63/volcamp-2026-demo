# OpenTelemetry signals

## Resource attributes

Every service emits:

- `service.namespace=demo-otel`
- `service.name`
- `service.instance.id`
- `deployment.environment.name`

## Application traces

- ASP.NET Core server spans
- `HttpClient` dependency spans
- `order.process`
- `inventory.check`
- timeout and retry span events
- W3C trace propagation between OrderService and InventoryService

## Metrics

- standard ASP.NET Core request metrics
- standard `HttpClient` dependency metrics
- .NET runtime metrics
- `demo.orders`
- `demo.order.processing.duration`
- `demo.inventory.faults`
- `demo.inventory.lookup.duration`
- `demo.http.retries`
- `demo.agent.invocations`
- `demo.agent.llm.retries`

Metric labels are bounded. Order IDs, trace IDs, arbitrary prompts and arbitrary SKUs
are not metric dimensions.

## Logs

Logs include formatted messages and scopes. OpenTelemetry correlates them with
`trace_id` and `span_id`. Important events are:

- order accepted;
- deterministic inventory latency injected;
- inventory timeout;
- retry scheduled;
- final 504 response;
- read-only MCP tool completed;
- Azure OpenAI retry.

## Agent telemetry

The explicit root span is `invoke_agent`. Microsoft Agent Framework instruments the
chat client and tool path using current OpenTelemetry GenAI conventions. Replay mode
also emits:

- `execute_tool get_service_health`
- `execute_tool get_dependency_metrics`
- `execute_tool find_slow_traces`
- `execute_tool get_trace_summary`
- `execute_tool search_correlated_logs`
- `chat conference-replay`

Replay includes synthetic input/output token counts and is tagged
`demo.mode=replay`. Live spans capture model, durations, status and provider token
usage when available.

Prompts, responses, tool arguments and tool results are not captured as span content.
# Estimated GenAI cost

The aggregate Agent Framework `invoke_agent` span is enriched at export time with:

- `demo.gen_ai.estimated_cost.usd`;
- `demo.gen_ai.estimated_input_cost.usd`;
- `demo.gen_ai.estimated_output_cost.usd`;
- the three per-million pricing attributes;
- `demo.gen_ai.pricing.type=estimated`.

The `demo.gen_ai.estimated_cost` counter records the same amount in USD for
Aspire and Prometheus. The defaults are GPT-4.1 mini Global rates. They can be
overridden under `AzureOpenAI:Pricing` in the ignored `appsettings.Local.json`.
Reasoning tokens are already included in output tokens and are not billed twice.
Replay spans do not contain aggregate live usage and therefore do not increment
the cost metric.

# Agent guard

Each live run is protected by `AgentExecutionGuard` (`src\DiagnosticAgent\Guard`).
Limits come from the `AgentGuard` configuration section. Profiles can only tighten them.

| Limit | Default | `Strict` profile |
|---|---|---|
| Duration | 90 s | 90 s |
| Total tokens (input + output) | 60 000 | 8 000 |
| Estimated cost | 0.05 USD | 0.05 USD |
| Tool calls | 8 | 2 |
| Global budget (Prometheus, sliding 1 h) | 0.50 USD | 0.50 USD |

When each check happens:

1. **Before the run:** `GlobalBudgetChecker` evaluates
   `sum(increase({__name__=~"demo_gen_ai_estimated_cost.*_total"}[1h]))` in Prometheus.
   If the budget is exceeded, the API returns HTTP 429 and the agent is not started.
   If Prometheus is unavailable, the check fails open by default and the error is recorded.
   The metric arrives with a delay of a few seconds.
2. **During the whole run:** `CancelAfter(MaxDuration)` cancels the in-flight LLM or MCP call.
3. **After each chat response:** `GuardedChatClient` adds `UsageDetails` to the running totals.
   These are the same numbers as `gen_ai.usage.*`, and cost uses the shared `GenAiPricing`.
4. **Before each chat call:** if the token or cost budget has been reached, the model is not called.
5. **Before each tool call:** `GuardedAIFunction` enforces the tool-call limit. A refused tool is
   not executed, and the function-invocation loop is terminated.

A single LLM call can overshoot the token or cost budget, because checks run between calls.

When the guard is triggered, the API returns HTTP 200 with `guardTriggered=true` and a partial report in French.
No further model call is made. The telemetry records:

- on the root `invoke_agent` span: status `Error`, the events `guard.triggered` and
  `guard.global_budget.checked`, and the attributes `demo.agent.guard.profile`,
  `demo.agent.guard.limit.*`, `demo.agent.guard.consumed.*`, `demo.agent.guard.triggered`,
  `demo.agent.guard.reason` (`duration|tokens|cost|tool_calls|global_budget`) and
  `demo.agent.guard.global.*`;
- on the refused `execute_tool` span: a `guard.triggered` event;
- metric `demo.agent.guard.triggers{demo.agent.guard.reason}` (Prometheus: `demo_agent_guard_triggers_total`);
- metric `demo.agent.guard.usage_ratio{demo.agent.guard.dimension,demo.agent.guard.profile}`;
- metric `demo.agent.invocations{demo.outcome=guard_triggered}`.

Replay is not subject to the guard, because it makes no model call.
