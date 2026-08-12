import type { MiddlewareHandler } from "hono";
import type { Env } from "../env";

/**
 * Port of CaravanCMS.Api/Middleware/ApiKeyMiddleware.cs.
 * Header name and behavior match exactly: X-API-Key, ordinal comparison,
 * 401 with the same two error message shapes. /health and /addin/* are
 * exempted at the route-mounting level (see index.ts), not here — same
 * split as the original (UseStaticFiles before UseMiddleware<ApiKeyMiddleware>).
 */
export const apiKeyMiddleware: MiddlewareHandler<{ Bindings: Env }> = async (c, next) => {
  const provided = c.req.header("X-API-Key");

  if (!provided) {
    return c.json({ error: "API key required. Include X-API-Key header." }, 401);
  }

  if (provided !== c.env.API_KEY) {
    return c.json({ error: "Invalid API key." }, 401);
  }

  await next();
};
