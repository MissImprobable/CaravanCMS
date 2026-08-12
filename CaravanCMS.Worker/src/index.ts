import { Hono } from "hono";
import { cors } from "hono/cors";
import type { Env } from "./env";
import { apiKeyMiddleware } from "./middleware/apiKey";
import manifestTemplate from "./lib/manifest.template.xml";

import caravans from "./routes/caravans";
import customers from "./routes/customers";
import conversations from "./routes/conversations";
import documents from "./routes/documents";
import jobs from "./routes/jobs";
import importRoutes from "./routes/import";

const app = new Hono<{ Bindings: Env }>();

// Matches the current API's AllowAnyOrigin/AnyMethod/AnyHeader — auth is the
// API key, not origin, and the Outlook add-in's WebView2 origin needs this.
app.use("*", cors());

// GET /health — no auth, matches Program.cs.
app.get("/health", (c) => c.json({ status: "healthy", timestamp: new Date().toISOString() }));

// GET /addin/manifest.xml — no auth. Renders the template, substituting every
// {{BASE_URL}} occurrence, same as the C# minimal-API route in Program.cs.
// Everything else under /addin/* (taskpane.html/js/css, icons) is served
// directly by the Workers static-assets binding (public/addin/*) before this
// Worker script even runs — no route needed for those here.
app.get("/addin/manifest.xml", (c) => {
  const baseUrl = c.env.PUBLIC_BASE_URL.replace(/\/$/, "");
  const xml = manifestTemplate.replaceAll("{{BASE_URL}}", baseUrl);
  return c.text(xml, 200, { "Content-Type": "application/xml" });
});

// Everything under /api/* requires X-API-Key (matches ApiKeyMiddleware.cs).
app.use("/api/*", apiKeyMiddleware);

app.route("/api/caravans", caravans);
app.route("/api/customers", customers);
app.route("/api/conversations", conversations);
app.route("/api/documents", documents);
app.route("/api/jobs", jobs);
app.route("/api/import", importRoutes);

export default app;
