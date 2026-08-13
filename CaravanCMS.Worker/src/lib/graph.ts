import type { Env } from "../env";

/**
 * App-only Microsoft Graph auth (client-credentials flow) for the "log entire
 * conversation" feature — reads any tenant mailbox's messages via Mail.Read
 * (Application permission, admin-consented), no per-user sign-in needed.
 * Token is cached at module scope so warm Worker isolates reuse it instead of
 * re-authenticating every request; Cloudflare may still cold-start at any time,
 * which just means one extra token fetch.
 */
let cachedToken: { token: string; expiresAt: number } | null = null;

async function getGraphToken(env: Env): Promise<string> {
  const now = Date.now();
  if (cachedToken && cachedToken.expiresAt > now + 60_000) return cachedToken.token;

  const res = await fetch(`https://login.microsoftonline.com/${env.GRAPH_TENANT_ID}/oauth2/v2.0/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: env.GRAPH_CLIENT_ID,
      client_secret: env.GRAPH_CLIENT_SECRET,
      scope: "https://graph.microsoft.com/.default",
      grant_type: "client_credentials",
    }),
  });
  if (!res.ok) {
    throw new Error(`Graph token request failed: ${res.status} ${await res.text()}`);
  }
  const data = await res.json<{ access_token: string; expires_in: number }>();
  cachedToken = { token: data.access_token, expiresAt: now + data.expires_in * 1000 };
  return cachedToken.token;
}

export interface GraphMessage {
  from: string | null;
  toRecipients: string[];
  receivedDateTime: string;
  internetMessageId: string | null;
  subject: string | null;
  bodyText: string;
}

interface GraphMessageRaw {
  from?: { emailAddress?: { address?: string } };
  toRecipients?: { emailAddress?: { address?: string } }[];
  receivedDateTime: string;
  internetMessageId?: string;
  subject?: string;
  uniqueBody?: { content?: string };
}

/**
 * Fetches every message in a conversation from a specific mailbox, oldest first.
 * Uses `uniqueBody` (Exchange's own quote-chain stripping) rather than the regex
 * trimmer used elsewhere — Graph already knows exactly where each message's own
 * content ends, which is more reliable than pattern-matching client-varying
 * "-----Original Message-----" style markers.
 */
export async function fetchConversationMessages(
  env: Env,
  mailbox: string,
  conversationId: string,
): Promise<GraphMessage[]> {
  const token = await getGraphToken(env);
  const filter = `conversationId eq '${conversationId.replace(/'/g, "''")}'`;
  const url =
    `https://graph.microsoft.com/v1.0/users/${encodeURIComponent(mailbox)}/messages` +
    `?$filter=${encodeURIComponent(filter)}` +
    `&$select=from,toRecipients,receivedDateTime,internetMessageId,subject,uniqueBody` +
    `&$orderby=receivedDateTime asc&$top=100`;

  const res = await fetch(url, {
    headers: {
      Authorization: `Bearer ${token}`,
      Prefer: 'outlook.body-content-type="text"',
    },
  });
  if (!res.ok) {
    throw new Error(`Graph messages request failed: ${res.status} ${await res.text()}`);
  }
  const data = await res.json<{ value: GraphMessageRaw[] }>();

  return data.value.map((m) => ({
    from: m.from?.emailAddress?.address ?? null,
    toRecipients: (m.toRecipients ?? []).map((r) => r.emailAddress?.address).filter((a): a is string => !!a),
    receivedDateTime: m.receivedDateTime,
    internetMessageId: m.internetMessageId ?? null,
    subject: m.subject ?? null,
    bodyText: (m.uniqueBody?.content ?? "").trim(),
  }));
}
