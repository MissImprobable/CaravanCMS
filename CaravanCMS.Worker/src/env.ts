/** Cloudflare bindings + vars/secrets for this Worker (mirrors wrangler.toml). */
export interface Env {
  DB: D1Database;
  DOCUMENTS: R2Bucket;
  ASSETS: Fetcher;
  PUBLIC_BASE_URL: string;
  /** Secret — set via `wrangler secret put API_KEY`, never committed. */
  API_KEY: string;
}
