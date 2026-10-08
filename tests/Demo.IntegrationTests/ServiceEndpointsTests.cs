using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Demo.IntegrationTests;

public sealed class ServiceEndpointsTests
{
    [Fact]
    public async Task Inventory_fast_sku_returns_without_fault()
    {
        await using var factory = new WebApplicationFactory<InventoryService.AssemblyMarker>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/inventory/DEMO-FAST?quantity=2");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("DEMO-FAST", payload.GetProperty("sku").GetString());
        Assert.Equal(2, payload.GetProperty("requestedQuantity").GetInt32());
        Assert.False(payload.GetProperty("faultInjected").GetBoolean());
    }

    [Fact]
    public async Task Agent_replay_is_available_without_Azure_credentials()
    {
        await using var factory = new WebApplicationFactory<DiagnosticAgent.AssemblyMarker>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/diagnose?mode=replay",
            new { question = "Why are orders failing?" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("replay", payload.GetProperty("mode").GetString());
        Assert.Contains("700 ms", payload.GetProperty("diagnosis").GetString());
        Assert.False(payload.GetProperty("sensitiveContentCaptured").GetBoolean());
    }
}
