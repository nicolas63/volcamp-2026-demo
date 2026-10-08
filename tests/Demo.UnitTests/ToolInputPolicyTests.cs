using ObservabilityMcp;

namespace Demo.UnitTests;

public sealed class ToolInputPolicyTests
{
    [Theory]
    [InlineData("order-service")]
    [InlineData("inventory-service")]
    [InlineData("diagnostic-agent")]
    [InlineData("observability-mcp")]
    public void Service_accepts_only_allowlisted_names(string serviceName)
    {
        Assert.Equal(serviceName, ToolInputPolicy.Service(serviceName));
    }

    [Fact]
    public void Service_rejects_non_allowlisted_names()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolInputPolicy.Service("unknown-service"));
    }

    [Fact]
    public void Dependency_port_accepts_only_the_demo_edge()
    {
        Assert.Equal(
            "5102",
            ToolInputPolicy.DependencyServerPort("order-service", "inventory-service"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ToolInputPolicy.DependencyServerPort("inventory-service", "order-service"));
    }

    [Theory]
    [InlineData("4f1c2e4a9d5b4d37a84a7f3c2d11e900")]
    [InlineData("00000000000000000000000000000000")]
    public void Trace_id_accepts_lowercase_w3c_identifiers(string traceId)
    {
        Assert.Equal(traceId, ToolInputPolicy.TraceId(traceId));
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("4F1C2E4A9D5B4D37A84A7F3C2D11E900")]
    [InlineData("../secret")]
    public void Trace_id_rejects_invalid_values(string traceId)
    {
        Assert.Throws<ArgumentException>(() => ToolInputPolicy.TraceId(traceId));
    }
}
