using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Demo.UnitTests;

public sealed class InventoryClientTests
{
    [Fact]
    public async Task CheckAsync_times_out_three_times_then_fails()
    {
        var handler = new TimeoutHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://inventory-service")
        };
        var client = new InventoryClient(httpClient);
        var configuration = new ConfigurationManager
        {
            ["Inventory:TimeoutMs"] = "10",
            ["Inventory:RetryDelaysMs:0"] = "1",
            ["Inventory:RetryDelaysMs:1"] = "1"
        };
        using var telemetry = new OrderTelemetry("order-test");

        var exception = await Assert.ThrowsAsync<InventoryTimeoutException>(() =>
            client.CheckAsync(
                "VOLCAMP-63",
                1,
                configuration,
                telemetry,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(3, exception.Attempts);
        Assert.Equal(3, handler.RequestCount);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout token should cancel the request.");
        }
    }
}
