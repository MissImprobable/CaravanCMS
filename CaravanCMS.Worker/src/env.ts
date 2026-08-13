/** Cloudflare bindings + vars/secrets for this Worker (mirrors wrangler.toml). */
export interface Env {
  DB: D1Database;
  DOCUMENTS: R2Bucket;
  ASSETS: Fetcher;
  PUBLIC_BASE_URL: string;
  /** Secret — set via `wrangler secret put API_KEY`, never committed. */
  API_KEY: string;
  /** Secrets for the "log entire conversation" feature — app-only Microsoft Graph
   *  client-credentials auth (Mail.Read, Application permission, tenant-wide, admin
   *  consent granted). Set via `wrangler secret put GRAPH_CLIENT_ID` etc, never committed. */
  GRAPH_TENANT_ID: string;
  GRAPH_CLIENT_ID: string;
  GRAPH_CLIENT_SECRET: string;
}
