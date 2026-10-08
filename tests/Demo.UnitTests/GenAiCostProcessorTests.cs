using System.Diagnostics;
using Demo.ServiceDefaults;

namespace Demo.UnitTests;

public sealed class GenAiCostProcessorTests
{
    [Fact]
    public void Processor_enriches_aggregate_agent_span_with_estimated_cost()
    {
        using var processor = new GenAiCostProcessor("test", 0.40, 0.10, 1.60);
        using var activity = new Activity("invoke_agent test").Start();
        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        activity.SetTag("gen_ai.request.model", "gpt-4.1-mini");
        activity.SetTag("gen_ai.usage.input_tokens", 20_801L);
        activity.SetTag("gen_ai.usage.cache_read.input_tokens", 4_096L);
        activity.SetTag("gen_ai.usage.output_tokens", 3_756L);

        processor.OnEnd(activity);

        var cost = Assert.IsType<double>(
            activity.GetTagItem("demo.gen_ai.estimated_cost.usd"));
        Assert.Equal(0.0131012, cost, precision: 7);
        Assert.Equal(
            "estimated",
            activity.GetTagItem("demo.gen_ai.pricing.type"));
    }
}
