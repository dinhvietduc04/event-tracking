(() => {
  const byId = (id) => document.getElementById(id);
  const cart = new Map();
  let products = [];
  let currency = "USD";
  let pendingOrder = null;
  let placing = false;
  let readingEvents = false;
  const money = (amount) =>
    new Intl.NumberFormat("en-US", { style: "currency", currency }).format(
      amount / 100,
    );
  const itemCount = () =>
    [...cart.values()].reduce((sum, quantity) => sum + quantity, 0);
  const total = () =>
    products.reduce(
      (sum, product) => sum + product.priceMinor * (cart.get(product.id) || 0),
      0,
    );
  const textNode = (tag, className, text) => {
    const node = document.createElement(tag);
    node.className = className;
    node.textContent = text;
    return node;
  };

  async function request(path, body) {
    const response = await fetch(`/demo/${path}`, {
      method: body === undefined ? "GET" : "POST",
      credentials: "same-origin",
      headers: body === undefined ? {} : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(15000),
    });
    if (!response.ok) {
      const payload = await response.json().catch(() => ({}));
      const error = new Error(
        payload.error ||
          `Request failed (${response.status}). Please try again.`,
      );
      error.status = response.status;
      throw error;
    }
    return response.status === 202 ? null : response.json();
  }

  async function track(eventType, productId = null) {
    try {
      await request("events", { eventType, productId });
      byId("tracking-status").textContent = "";
    } catch {
      byId("tracking-status").textContent =
        "An activity event could not be sent. You can still use the shop.";
    }
  }

  function renderProducts() {
    byId("products").replaceChildren();
    for (const product of products) {
      const card = document.createElement("article");
      card.className = "product-card";
      const art = document.createElement("div");
      art.className = `product-art ${product.color} ${product.id}`;
      art.setAttribute("aria-hidden", "true");
      art.append(textNode("div", "object", ""));
      const info = textNode("div", "product-info", "");
      info.append(
        textNode("h3", "", product.name),
        textNode("span", "price", money(product.priceMinor)),
      );
      const add = textNode("button", "add-button", "Add to bag");
      add.setAttribute("aria-label", `Add ${product.name} to bag`);
      add.append(textNode("span", "", "+"));
      add.addEventListener("click", () => {
        if (pendingOrder || (cart.get(product.id) || 0) >= 10) return;
        cart.set(product.id, (cart.get(product.id) || 0) + 1);
        renderCart();
        byId("shop-status").textContent = `${product.name} added to your bag.`;
        void track("product_added", product.id);
      });
      add.dataset.product = product.id;
      card.append(
        art,
        info,
        textNode("p", "product-description", product.description),
        add,
      );
      byId("products").append(card);
    }
  }

  function renderCart() {
    const container = byId("cart-items");
    container.replaceChildren();
    for (const [id, quantity] of cart) {
      const product = products.find((item) => item.id === id);
      const row = textNode("div", "cart-row", "");
      const description = textNode("div", "", "");
      description.append(
        textNode("div", "cart-name", product.name),
        textNode("div", "cart-price", money(product.priceMinor * quantity)),
      );
      const controls = textNode("div", "quantity", "");
      for (const delta of [-1, 1]) {
        const button = textNode("button", "", delta === 1 ? "+" : "−");
        button.setAttribute(
          "aria-label",
          `${delta === 1 ? "Add one" : "Remove one"} ${product.name}`,
        );
        button.disabled = !!pendingOrder || (delta === 1 && quantity >= 10);
        button.addEventListener("click", () => {
          if (pendingOrder) return;
          const next = quantity + delta;
          if (next === 0) cart.delete(id);
          else cart.set(id, next);
          renderCart();
          byId("shop-status").textContent = "Bag updated.";
          if (delta === 1) void track("product_added", id);
        });
        controls.append(button);
        if (delta === -1) controls.append(textNode("span", "", quantity));
      }
      row.append(description, controls);
      container.append(row);
    }
    if (cart.size === 0)
      container.append(
        textNode(
          "p",
          "empty-cart",
          "A little room for something good. Your bag is empty.",
        ),
      );
    byId("bag-count").textContent = itemCount();
    byId("cart-total").textContent = money(total());
    byId("checkout").disabled = cart.size === 0 || placing;
    document.querySelectorAll("[data-product]").forEach((button) => {
      button.disabled =
        !!pendingOrder || (cart.get(button.dataset.product) || 0) >= 10;
    });
  }

  async function refreshEvents() {
    if (readingEvents || document.hidden) return;
    readingEvents = true;
    try {
      const events = await request("events");
      byId("feed-status").textContent = "● Live · every 2s";
      byId("browser-count").textContent = events.filter(
        (event) => event.source === "browser",
      ).length;
      byId("server-count").textContent = events.filter(
        (event) => event.source === "server",
      ).length;
      const rows = events.map((event) => {
        const row = textNode("div", "event-row", "");
        const details = event.properties || {};
        const product = products.find((item) => item.id === details.productId);
        const detail =
          event.source === "server"
            ? `${details.itemCount} item(s) · ${money(details.amountMinor)} · ${String(details.orderId).slice(0, 8)}`
            : product?.name ||
              (event.eventType === "page_view"
                ? "The everyday collection"
                : "Demo checkout opened");
        row.append(
          textNode(
            "time",
            "event-time",
            new Date(event.occurredAt).toLocaleTimeString([], {
              hour12: false,
            }),
          ),
          textNode(
            "span",
            `source-badge ${event.source}`,
            event.source === "server" ? "Server" : "Browser",
          ),
          textNode("span", "event-name", event.eventType),
          textNode("span", "event-detail", detail),
        );
        return row;
      });
      byId("events").replaceChildren(...rows);
      if (!rows.length)
        byId("events").append(
          textNode("p", "feed-empty", "Your first event is on its way."),
        );
    } catch {
      byId("feed-status").textContent = "Reconnecting…";
    } finally {
      readingEvents = false;
    }
  }

  byId("checkout").addEventListener("click", () => {
    if (cart.size === 0) return;
    byId("checkout-total").textContent = money(total());
    byId("checkout-dialog").showModal();
    void track("checkout_started");
  });
  byId("cancel-checkout").addEventListener("click", () =>
    byId("checkout-dialog").close(),
  );
  byId("checkout-dialog").addEventListener("cancel", (event) => {
    if (placing) event.preventDefault();
  });
  byId("close-receipt").addEventListener("click", () =>
    byId("receipt-dialog").close(),
  );

  byId("place-order").addEventListener("click", async () => {
    if (placing || cart.size === 0) return;
    pendingOrder ??= {
      orderId: crypto.randomUUID(),
      items: [...cart].map(([productId, quantity]) => ({
        productId,
        quantity,
      })),
    };
    placing = true;
    renderCart();
    byId("place-order").disabled = true;
    byId("cancel-checkout").disabled = true;
    byId("place-order").textContent = "Placing your demo order…";
    byId("order-error").textContent = "";
    try {
      const order = await request("orders", pendingOrder);
      cart.clear();
      pendingOrder = null;
      byId("checkout-dialog").close();
      byId("receipt-details").textContent =
        `Order ${order.id.slice(0, 8)} · ${order.itemCount} item(s) · ${money(order.totalMinor)}`;
      byId("receipt-dialog").showModal();
      byId("shop-status").textContent =
        "Demo order completed. Your bag is ready for another visit.";
      void refreshEvents();
    } catch (error) {
      if (error.status === 400) pendingOrder = null;
      byId("order-error").textContent =
        `${error.message} ${pendingOrder ? "Retry uses the same order ID; your bag is held until the result is confirmed." : ""}`;
    } finally {
      placing = false;
      byId("place-order").disabled = false;
      byId("cancel-checkout").disabled = false;
      byId("place-order").textContent = pendingOrder
        ? "Retry demo order"
        : "Place demo order";
      renderCart();
    }
  });

  async function initialize() {
    try {
      const data = await request("bootstrap");
      products = data.products;
      currency = data.currency;
      renderProducts();
      renderCart();
      await track("page_view");
      await refreshEvents();
      setInterval(refreshEvents, 2000);
      document.addEventListener("visibilitychange", () => {
        if (!document.hidden) void refreshEvents();
      });
    } catch {
      byId("products").replaceChildren(
        textNode(
          "p",
          "status",
          "The shop could not open. Run the API in Development mode, then refresh this page.",
        ),
      );
      byId("feed-status").textContent = "Offline";
    }
  }
  void initialize();
})();
