using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTracking.Api.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventTracking.Api.Tests;

public sealed class DemoShopTests : IClassFixture<PrototypeFactory>
{
    private readonly PrototypeFactory _factory;
    public DemoShopTests(PrototypeFactory factory) => _factory = factory;

    [Fact]
    public async Task BrowserAndServerEvents_ShareSessionAndHaveDistinctSources()
    {
        using var app = _factory.WithWebHostBuilder(_ => { });
        using var client = app.CreateClient();
        (await client.GetAsync("/demo/bootstrap")).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/demo/events", new { eventType = "page_view" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/demo/events", new { eventType = "checkout_started" })).EnsureSuccessStatusCode();
        Guid orderId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/demo/orders", new
        {
            orderId, items = new[] { new { productId = "mug", quantity = 2 } }, totalMinor = 1
        });
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<DemoOrder>();
        Assert.Equal(3600, order!.TotalMinor);

        var events = await ReadUntil(client, items => items.Length == 3);
        Assert.Equal(2, events.Count(item => item.GetProperty("source").GetString() == "browser"));
        var purchase = Assert.Single(events, item => item.GetProperty("source").GetString() == "server");
        Assert.Equal("purchase_completed", purchase.GetProperty("eventType").GetString());
        Assert.Equal(3600, purchase.GetProperty("properties").GetProperty("amountMinor").GetInt32());
        Assert.Single(events.Select(item => item.GetProperty("sessionId").GetString()).Distinct());

        using var otherSession = app.CreateClient();
        (await otherSession.GetAsync("/demo/bootstrap")).EnsureSuccessStatusCode();
        Assert.Empty((await otherSession.GetFromJsonAsync<JsonElement[]>("/demo/events"))!);
    }

    [Fact]
    public async Task ConcurrentOrderRetries_EmitOnePurchase_AndRejectChangedCart()
    {
        using var app = _factory.WithWebHostBuilder(_ => { });
        using var client = app.CreateClient();
        await client.GetAsync("/demo/bootstrap");
        var order = new DemoOrderRequest(Guid.NewGuid(), [new("tote", 1)]);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsJsonAsync("/demo/orders", order)));
        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal(order.OrderId, (await response.Content.ReadFromJsonAsync<DemoOrder>())!.Id);
        }

        // The sentinel is queued after all retries, so observing it proves those messages were processed.
        await client.PostAsJsonAsync("/demo/events", new { eventType = "checkout_started" });
        var events = await ReadUntil(client, items => items.Any(item => item.GetProperty("eventType").GetString() == "checkout_started"));
        Assert.Single(events, item => item.GetProperty("eventType").GetString() == "purchase_completed");
        var conflict = await client.PostAsJsonAsync("/demo/orders", order with { Items = [new("tote", 2)] });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task BrowserCannotEmitPurchase_AndInvalidOrderProducesNoPurchase()
    {
        using var app = _factory.WithWebHostBuilder(_ => { });
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/demo/events")).StatusCode);
        await client.GetAsync("/demo/bootstrap");
        var forged = await client.PostAsJsonAsync("/demo/events", new { eventType = "purchase_completed", source = "server" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        DemoCartItem[]?[] invalidCarts = [null, [], [new("unknown", 1)], [new("mug", 0)],
            [new("mug", 11)], [new("mug", 1), new("mug", 1)], [null!]];
        foreach (var items in invalidCarts)
        {
            var invalid = await client.PostAsJsonAsync("/demo/orders", new DemoOrderRequest(Guid.NewGuid(), items));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        // Opening/canceling checkout is browser activity, not a completed order.
        await client.PostAsJsonAsync("/demo/events", new { eventType = "checkout_started" });
        var events = await ReadUntil(client, items => items.Length > 0);
        Assert.All(events, item => Assert.Equal("browser", item.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task ShopAssetsAreAvailableInDevelopment_AndDemoIsOffOutsideDevelopment()
    {
        using var client = _factory.CreateClient();
        Assert.Contains("A make-believe shop", await client.GetStringAsync("/shop/index.html"));
        (await client.GetAsync("/shop/shop.js")).EnsureSuccessStatusCode();
        (await client.GetAsync("/shop/shop.css")).EnsureSuccessStatusCode();
        using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var productionClient = production.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.GetAsync("/demo/bootstrap")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.GetAsync("/shop/index.html")).StatusCode);
    }

    private static async Task<JsonElement[]> ReadUntil(HttpClient client, Func<JsonElement[], bool> complete)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var events = (await client.GetFromJsonAsync<JsonElement[]>("/demo/events"))!;
            if (complete(events)) return events;
            await Task.Delay(25);
        }
        Assert.Fail("Expected demo events were not processed before the timeout.");
        return [];
    }
}
