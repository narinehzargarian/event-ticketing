import { defineConfig } from "@playwright/test";

// Expects the API to be running. Starts the built web app itself.
export default defineConfig({
  testDir: "e2e",
  timeout: 30_000,
  use: { baseURL: "http://localhost:5174", browserName: "chromium", screenshot: "only-on-failure" },
  webServer: { command: "npx vite preview --port 5174 --strictPort", url: "http://localhost:5174", reuseExistingServer: true },
});
