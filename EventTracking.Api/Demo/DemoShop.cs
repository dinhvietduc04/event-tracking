using EventTracking.Api.Models;
using EventTracking.Api.Services;

namespace EventTracking.Api.Demo;

public sealed record DemoProduct(string Id, string Name, string Description, int PriceMinor, string Color);
public sealed record DemoCartItem(string ProductId, int Quantity);
public sealed record DemoOrderRequest(Guid OrderId, DemoCartItem[]? Items);
public sealed record DemoBrowserEventRequest(string? EventType, string? ProductId);
public sealed record DemoOrder(Guid Id, int TotalMinor, string Currency, int ItemCount);

// Deliberately volatile demo business state. No money, accounts, or personal details.
public sealed class DemoShop
{
    public static readonly IReadOnlyList<DemoProduct> Products = Array.AsReadOnly(new[]
    {
        new DemoProduct("mug", "Everyday mug", "A little pause, a good cup.", 1800, "peach"),
        new DemoProduct("tote", "Market tote", "For wherever the day goes.", 2400, "sage"),
        new DemoProduct("notebook", "Field notes", "Room for your next good idea.", 1200, "lavender")
    });

    private readonly SemaphoreSlim _orderLock = new(1, 1);
    private readonly Dictionary<(string Session, Guid Id), (string Fingerprint, DemoOrder Order)> _orders = [];

    public async Task<IResult> PlaceOrder(string session, DemoOrderRequest request,
        IEventQueue queue, CancellationToken cancellationToken)
    {
        if (request.OrderId == Guid.Empty || request.Items is not { Length: > 0 and <= 3 }
            || request.Items.Any(item => item is null || item.Quantity is < 1 or > 10
                || !Products.Any(product => product.Id == item.ProductId))
            || request.Items.Select(item => item.ProductId).Distinct().Count() != request.Items.Length)
        {
            return Results.BadRequest(new { error = "Choose 1–3 different products, with 1–10 of each, and a valid order ID." });
        }

        string fingerprint = string.Join(";", request.Items.OrderBy(item => item.ProductId)
            .Select(item => $"{item.ProductId}:{item.Quantity}"));
        await _orderLock.WaitAsync(cancellationToken);
        try
        {
            if (_orders.TryGetValue((session, request.OrderId), out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? Results.Ok(existing.Order)
                    : Results.Conflict(new { error = "That order ID was already used for a different cart." });
            }

            // Prices and purchase identity come from the shop backend, never browser telemetry.
            int total = request.Items.Sum(item => Products.Single(product => product.Id == item.ProductId).PriceMinor * item.Quantity);
            var order = new DemoOrder(request.OrderId, total, "USD", request.Items.Sum(item => item.Quantity));
            var now = DateTimeOffset.UtcNow;
            await queue.QueueAsync(new TrackedEvent(Guid.NewGuid(), "purchase_completed", null, now, now,
                "server", session, new Dictionary<string, object?>
                {
                    ["orderId"] = order.Id, ["amountMinor"] = order.TotalMinor,
                    ["currency"] = order.Currency, ["itemCount"] = order.ItemCount
                }), cancellationToken);
            _orders.Add((session, request.OrderId), (fingerprint, order));
            return Results.Ok(order);
        }
        finally
        {
            _orderLock.Release();
        }
    }
}

public static class DemoShopEndpoints
{
    private const string CookieName = "demo-shop-session";

    public static void MapDemoShop(this WebApplication app)
    {
        var group = app.MapGroup("/demo");
        group.MapGet("/bootstrap", (HttpContext context) =>
        {
            if (GetSession(context) is null)
            {
                context.Response.Cookies.Append(CookieName, Guid.NewGuid().ToString("N"), new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps,
                    Path = "/demo", IsEssential = true
                });
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { products = DemoShop.Products, currency = "USD" });
        });

        group.MapPost("/events", async (DemoBrowserEventRequest request, HttpContext context,
            IEventQueue queue, CancellationToken cancellationToken) =>
        {
            string? session = GetSession(context);
            if (session is null) return Results.Unauthorized();
            if (request.EventType is not ("page_view" or "product_added" or "checkout_started")
                || (request.EventType == "product_added" && !DemoShop.Products.Any(item => item.Id == request.ProductId)))
            {
                return Results.BadRequest(new { error = "Unsupported browser event or product." });
            }
            var now = DateTimeOffset.UtcNow;
            await queue.QueueAsync(new TrackedEvent(Guid.NewGuid(), request.EventType, null, now, now,
                "browser", session, new Dictionary<string, object?> { ["productId"] = request.ProductId }), cancellationToken);
            return Results.Accepted();
        });

        group.MapGet("/events", (HttpContext context, IEventStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            string? session = GetSession(context);
            return session is null ? Results.Unauthorized() : Results.Ok(store.GetRecent(session));
        });

        group.MapPost("/orders", async (DemoOrderRequest request, HttpContext context,
            DemoShop shop, IEventQueue queue, CancellationToken cancellationToken) =>
        {
            string? session = GetSession(context);
            return session is null ? Results.Unauthorized() : await shop.PlaceOrder(session, request, queue, cancellationToken);
        });
    }

    private static string? GetSession(HttpContext context)
        => context.Request.Cookies.TryGetValue(CookieName, out string? value)
            && Guid.TryParseExact(value, "N", out _) ? value : null;
}
