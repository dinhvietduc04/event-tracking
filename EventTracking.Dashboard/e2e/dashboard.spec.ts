import { test, expect } from "@playwright/test";

test("demo actions reach authorized charts, explorer and timeline; mobile remains usable", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const navigation = await page.goto("/");
  expect(navigation?.status()).toBe(200);
  await page
    .getByLabel("Username", { exact: true })
    .fill(process.env.DASHBOARD_TEST_USERNAME || "smoke-analyst");
  await page
    .getByLabel("Password", { exact: true })
    .fill(
      process.env.DASHBOARD_TEST_PASSWORD || "synthetic-local-smoke-password",
    );
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "The bigger picture." }),
  ).toBeVisible();
  // Each run owns a fresh project and therefore has deterministic counts without deleting existing data.
  await page.getByRole("button", { name: "New project" }).click();
  await page
    .getByLabel("Project name", { exact: true })
    .fill("Browser smoke " + Date.now());
  await page
    .getByRole("button", { name: "Create project", exact: true })
    .click();
  await expect(page.getByLabel("Project name", { exact: true })).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Refresh", exact: true }),
  ).toBeEnabled();
  await expect(
    page.getByText("Your first event will appear here."),
  ).toBeVisible();
  await page.screenshot({
    path: "test-results/dashboard-empty.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Demo shop" }).click();
  await expect(
    page.getByRole("heading", { name: "Meet your next event." }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Simulate customer login" }).click();
  await expect(
    page.getByRole("button", { name: "Signed in as demo-user-001" }),
  ).toBeVisible();
  await page
    .getByRole("combobox", { name: "Quantity", exact: true })
    .selectOption("2");
  await page.getByRole("button", { name: "Place mock order" }).click();
  await expect(
    page.getByRole("status").filter({ hasText: "Order confirmed" }),
  ).toContainText("$48.00");
  await page.screenshot({
    path: "test-results/dashboard-demo.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Explore events" }).click();
  await expect(
    page.getByRole("cell", { name: "purchase", exact: true }),
  ).toBeVisible();
  await expect(
    page
      .locator(".metric")
      .filter({ hasText: "Total events" })
      .locator("strong"),
  ).toHaveText("4");
  await page
    .getByRole("row")
    .filter({ hasText: "purchase" })
    .getByRole("button", { name: /^Details/ })
    .click();
  await expect(page.locator(".event-details pre")).toContainText("4800");
  await page.screenshot({
    path: "test-results/dashboard-events.png",
    fullPage: true,
  });
  await page.getByLabel("Property", { exact: true }).fill("source");
  await page.getByLabel("Value (JSON)", { exact: true }).fill('"server"');
  await page.getByRole("button", { name: "Apply", exact: true }).click();
  await expect(
    page
      .locator(".metric")
      .filter({ hasText: "Total events" })
      .locator("strong"),
  ).toHaveText("1");
  await page
    .getByRole("button", { name: "demo-user-001", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Activity for demo-user-001" }),
  ).toBeVisible();
  await page.getByLabel("Property", { exact: true }).fill("");
  await page.getByLabel("Value (JSON)", { exact: true }).fill("");
  await page.getByRole("button", { name: "Apply", exact: true }).click();
  await page.getByRole("button", { name: "Overview", exact: true }).click();
  await expect(
    page
      .locator(".metric")
      .filter({ hasText: "Total events" })
      .locator("strong"),
  ).toHaveText("4");
  await page.screenshot({
    path: "test-results/dashboard-overview.png",
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("heading", { name: "The bigger picture." }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBeTruthy();
  expect(
    (await page.getByLabel("From", { exact: true }).boundingBox())!.width,
  ).toBeGreaterThan(120);
  await expect(
    page.getByRole("button", { name: "Sign out", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "test-results/dashboard-mobile.png",
    fullPage: true,
  });
  await page.setViewportSize({ width: 1440, height: 1050 });
  // Surface a permission failure without showing the previous project's data.
  await page.route("**/dashboard-api/projects/*/overview?*", (route) =>
    route.fulfill({
      status: 403,
      contentType: "application/problem+json",
      body: '{"title":"Forbidden"}',
    }),
  );
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("no longer have access");
  await expect(page.locator(".metric")).toHaveCount(0);
  await page.unroute("**/dashboard-api/projects/*/overview?*");
  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Welcome back." }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});
