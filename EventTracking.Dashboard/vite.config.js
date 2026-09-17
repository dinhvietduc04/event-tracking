import { defineConfig } from "vite";
export default defineConfig({
  base: "/dashboard/",
  build: {
    outDir: "../EventTracking.Api/wwwroot/dashboard",
    emptyOutDir: true,
  },
  server: { proxy: { "/dashboard-api": "http://127.0.0.1:5191" } },
});
