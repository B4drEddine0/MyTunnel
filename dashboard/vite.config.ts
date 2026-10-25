/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// In development the dashboard runs on Vite and proxies the API to the gateway, so the session
// cookie stays same-origin. In production the gateway serves the built files itself.
const gateway = process.env.MYTUNNEL_GATEWAY ?? "http://localhost:5018";

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { "/api": { target: gateway, changeOrigin: false } } },
  build: { outDir: "dist", sourcemap: false },
  test: { environment: "jsdom", globals: true },
});
