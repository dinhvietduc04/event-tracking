import { defineConfig, devices } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e",
  workers: 1,
  fullyParallel: false,
  timeout: 60000,
  use: {
    baseURL: process.env.DASHBOARD_TEST_URL || "http://127.0.0.1:5399",
    actionTimeout: 10000,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: process.env.DASHBOARD_TEST_CONNECTION
    ? {
        command:
          "dotnet run --no-build --configuration Release --no-launch-profile --project ../EventTracking.Api --urls=http://127.0.0.1:5399",
        url: "http://127.0.0.1:5399/health/ready",
        timeout: 120000,
        reuseExistingServer: false,
        env: {
          ConnectionStrings__Tracking: process.env.DASHBOARD_TEST_CONNECTION,
          ASPNETCORE_ENVIRONMENT: "Development",
          Storage__Profile: "Hosted",
          Storage__MigrateOnStartup: "true",
          Dashboard__Enabled: "true",
          Dashboard__Bootstrap__Username:
            process.env.DASHBOARD_TEST_USERNAME || "smoke-analyst",
          Dashboard__Bootstrap__Password:
            process.env.DASHBOARD_TEST_PASSWORD ||
            "synthetic-local-smoke-password",
          Dashboard__Bootstrap__Projects: "smoke",
          Logging__LogLevel__Default: "Warning",
          PORT: "",
        },
      }
    : undefined,
  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 1440, height: 1050 },
      },
    },
  ],
});
