import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: { outDir: "dist", emptyOutDir: true, sourcemap: false },
  server: {
    port: 5173,
    proxy: {
      "/api":   { target: "http://localhost:5000", changeOrigin: true },
      "/hubs":  { target: "http://localhost:5000", changeOrigin: true, ws: true },
      "/signin-oidc":  { target: "http://localhost:5000", changeOrigin: true },
      "/signout-callback-oidc": { target: "http://localhost:5000", changeOrigin: true },
    },
  },
});
