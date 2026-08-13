import { Hono } from "hono";
import type { Env } from "../env";
import type { ConversationRow, CommunicationLogRow, TagRow } from "../lib/rows";
import { toConversationDto, toCommunicationLogDto, toTagDto } from "../lib/mappers";
import { trimQuotedReplyChain } from "../lib/replyChain";
import { fetchConversationMessages } from "../lib/graph";

const app = new Hono<{ Bindings: Env }>();

// Same staff-domain check as the add-in's taskpane.js isStaffAddress() — used here to
// classify each Graph-fetched message's direction the same way the single-email flow does.
const STAFF_EMAIL_DOMAIN = "caravanland.co.nz";
function isStaffAddress(address: string | null | undefined): boolean {
  return new RegExp(`@${STAFF_EMAIL_DOMAIN}$`, "i").test((address || "").trim());
}

async function loadConversationDto(db: D1Database, id: number) {
  const conv = await db.prepare("SELECT * FROM Conversations WHERE Id = ?").bind(id).first<ConversationRow>();
  if (!conv) return null;
  const [tags, messages] = await Promise.all([
    db
      .prepare(`SELECT t.* FROM Tags t JOIN ConversationTags ct ON ct.TagsId = t.Id WHERE ct.ConversationsId = ?`)
      .bind(id)
      .all<TagRow>(),
    db.prepare("SELECT * FROM CommunicationLogs WHERE ConversationId = ?").bind(id).all<CommunicationLogRow>(),
  ]);
  return toConversationDto(conv, tags.results, messages.results);
}

/** Port of ConversationsController.Create — idempotent by CustomerId+ExternalConversationId. */
app.post("/", async (c) => {
  const body = await c.req.json<{
    customerId: number;
    registrationNumber?: string | null;
    subject?: string | null;
    externalConversationId?: string | null;
  }>();

  const customerExists = await c.env.DB.prepare("SELECT 1 FROM Customers WHERE Id = ?")
    .bind(body.customerId)
    .first();
  if (!customerExists) {
    return c.json({ error: `Customer ${body.customerId} not found.` }, 404);
  }

  if (body.externalConversationId && body.externalConversationId.trim()) {
    const existing = await c.env.DB.prepare(
      "SELECT Id FROM Conversations WHERE CustomerId = ? AND ExternalConversationId = ?",
    )
      .bind(body.customerId, body.externalConversationId)
      .first<{ Id: number }>();
    if (existing) {
      return c.json(await loadConversationDto(c.env.DB, existing.Id));
    }
  }

  if (body.registrationNumber && body.registrationNumber.trim()) {
    const caravanExists = await c.env.DB.prepare("SELECT 1 FROM Caravans WHERE RegistrationNumber = ?")
      .bind(body.registrationNumber)
      .first();
    if (!caravanExists) {
      return c.json({ error: `Caravan ${body.registrationNumber} not found.` }, 404);
    }
  }

  const now = new Date().toISOString();
  const result = await c.env.DB.prepare(
    `INSERT INTO Conversations (CustomerId, RegistrationNumber, Subject, ExternalConversationId, StartedAt, LastActivityAt)
     VALUES (?, ?, ?, ?, ?, ?)`,
  )
    .bind(body.customerId, body.registrationNumber ?? null, body.subject ?? null, body.externalConversationId ?? null, now, now)
    .run();

  const newId = result.meta.last_row_id;
  return c.json(await loadConversationDto(c.env.DB, newId));
});

/**
 * Logs an entire email thread in one call — pulls every message in the conversation from
 * the given mailbox via Microsoft Graph (app-only, Mail.Read) instead of relying on the
 * add-in opening each message one at a time. Idempotent the same way the single-message
 * flow is: re-running it after new replies arrive just adds what's new, skipping messages
 * already logged by ExternalMessageId.
 */
app.post("/log-thread", async (c) => {
  const body = await c.req.json<{
    mailbox: string;
    conversationId: string;
    customerId: number;
    subject?: string | null;
    registrationNumber?: string | null;
    loggedBy?: string | null;
  }>();

  if (!body.mailbox || !body.conversationId || !body.customerId) {
    return c.json({ error: "mailbox, conversationId, and customerId are required." }, 400);
  }

  const customerExists = await c.env.DB.prepare("SELECT 1 FROM Customers WHERE Id = ?")
    .bind(body.customerId)
    .first();
  if (!customerExists) {
    return c.json({ error: `Customer ${body.customerId} not found.` }, 404);
  }

  let messages;
  try {
    messages = await fetchConversationMessages(c.env, body.mailbox, body.conversationId);
  } catch (err) {
    return c.json({ error: `Failed to fetch conversation from Graph: ${(err as Error).message}` }, 502);
  }
  if (messages.length === 0) {
    return c.json({ error: "No messages found for this conversation." }, 404);
  }

  const now = new Date().toISOString();
  let conv = await c.env.DB.prepare(
    "SELECT * FROM Conversations WHERE CustomerId = ? AND ExternalConversationId = ?",
  )
    .bind(body.customerId, body.conversationId)
    .first<ConversationRow>();

  if (!conv) {
    const result = await c.env.DB.prepare(
      `INSERT INTO Conversations (CustomerId, RegistrationNumber, Subject, ExternalConversationId, StartedAt, LastActivityAt)
       VALUES (?, ?, ?, ?, ?, ?)`,
    )
      .bind(
        body.customerId,
        body.registrationNumber ?? null,
        body.subject ?? messages[0].subject ?? null,
        body.conversationId,
        now,
        now,
      )
      .run();
    conv = await c.env.DB.prepare("SELECT * FROM Conversations WHERE Id = ?")
      .bind(result.meta.last_row_id)
      .first<ConversationRow>();
  }

  let messagesAdded = 0;
  let messagesSkipped = 0;
  let latestActivity = conv!.LastActivityAt;

  for (const m of messages) {
    if (m.internetMessageId) {
      const dupe = await c.env.DB.prepare("SELECT 1 FROM CommunicationLogs WHERE ExternalMessageId = ?")
        .bind(m.internetMessageId)
        .first();
      if (dupe) {
        messagesSkipped++;
        continue;
      }
    }

    const direction = isStaffAddress(m.from) ? "Outbound" : "Inbound";
    const occurredAt = m.receivedDateTime;

    await c.env.DB.prepare(
      `INSERT INTO CommunicationLogs (ConversationId, Type, Direction, FromAddress, Body, ExternalMessageId, LoggedBy, OccurredAt, CreatedAt)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
      .bind(conv!.Id, "Email", direction, m.from, m.bodyText.slice(0, 20000), m.internetMessageId, body.loggedBy ?? null, occurredAt, now)
      .run();
    messagesAdded++;
    if (occurredAt > latestActivity) latestActivity = occurredAt;
  }

  if (latestActivity > conv!.LastActivityAt) {
    await c.env.DB.prepare("UPDATE Conversations SET LastActivityAt = ? WHERE Id = ?").bind(latestActivity, conv!.Id).run();
  }

  return c.json({
    conversation: await loadConversationDto(c.env.DB, conv!.Id),
    messagesAdded,
    messagesSkipped,
  });
});

/** Port of ConversationsController.Delete — only if the conversation has zero messages. */
app.delete("/:id", async (c) => {
  const id = Number(c.req.param("id"));
  const conv = await c.env.DB.prepare("SELECT Id FROM Conversations WHERE Id = ?").bind(id).first();
  if (!conv) {
    return c.json({ error: `Conversation ${id} not found.` }, 404);
  }

  const count = await c.env.DB.prepare("SELECT COUNT(*) c FROM CommunicationLogs WHERE ConversationId = ?")
    .bind(id)
    .first<{ c: number }>();
  if ((count?.c ?? 0) > 0) {
    return c.json({ error: `Conversation ${id} has ${count!.c} message(s) — remove them first.` }, 409);
  }

  await c.env.DB.prepare("DELETE FROM Conversations WHERE Id = ?").bind(id).run();
  return c.body(null, 204);
});

/** Port of ConversationsController.AddMessage — dedup by ExternalMessageId. */
app.post("/:id/messages", async (c) => {
  const id = Number(c.req.param("id"));
  const body = await c.req.json<{
    type: string;
    direction: string;
    fromAddress?: string | null;
    body?: string | null;
    externalMessageId?: string | null;
    loggedBy?: string | null;
    occurredAt?: string | null;
  }>();

  const conv = await c.env.DB.prepare("SELECT * FROM Conversations WHERE Id = ?").bind(id).first<ConversationRow>();
  if (!conv) {
    return c.json({ error: `Conversation ${id} not found.` }, 404);
  }

  if (body.externalMessageId && body.externalMessageId.trim()) {
    const dupe = await c.env.DB.prepare("SELECT * FROM CommunicationLogs WHERE ExternalMessageId = ?")
      .bind(body.externalMessageId)
      .first<CommunicationLogRow>();
    if (dupe) {
      return c.json(toCommunicationLogDto(dupe));
    }
  }

  const occurredAt = body.occurredAt ?? new Date().toISOString();
  const now = new Date().toISOString();

  const result = await c.env.DB.prepare(
    `INSERT INTO CommunicationLogs (ConversationId, Type, Direction, FromAddress, Body, ExternalMessageId, LoggedBy, OccurredAt, CreatedAt)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  )
    .bind(id, body.type, body.direction, body.fromAddress ?? null, body.body ?? null, body.externalMessageId ?? null, body.loggedBy ?? null, occurredAt, now)
    .run();

  if (occurredAt > conv.LastActivityAt) {
    await c.env.DB.prepare("UPDATE Conversations SET LastActivityAt = ? WHERE Id = ?").bind(occurredAt, id).run();
  }

  const newMessage = await c.env.DB.prepare("SELECT * FROM CommunicationLogs WHERE Id = ?")
    .bind(result.meta.last_row_id)
    .first<CommunicationLogRow>();
  return c.json(toCommunicationLogDto(newMessage!));
});

/** Port of ConversationsController.RemoveMessage — "undo"; deletes an empty conversation too. */
app.delete("/:id/messages/:messageId", async (c) => {
  const id = Number(c.req.param("id"));
  const messageId = Number(c.req.param("messageId"));

  const message = await c.env.DB.prepare(
    "SELECT Id FROM CommunicationLogs WHERE Id = ? AND ConversationId = ?",
  )
    .bind(messageId, id)
    .first();
  if (!message) {
    return c.json({ error: `Message ${messageId} not found on conversation ${id}.` }, 404);
  }

  await c.env.DB.prepare("DELETE FROM CommunicationLogs WHERE Id = ?").bind(messageId).run();

  const remaining = await c.env.DB.prepare("SELECT COUNT(*) c FROM CommunicationLogs WHERE ConversationId = ?")
    .bind(id)
    .first<{ c: number }>();
  if ((remaining?.c ?? 0) === 0) {
    await c.env.DB.prepare("DELETE FROM Conversations WHERE Id = ?").bind(id).run();
  }

  return c.body(null, 204);
});

/** Port of ConversationsController.AddTag — tags are scoped per customer. */
app.post("/:id/tags", async (c) => {
  const id = Number(c.req.param("id"));
  const body = await c.req.json<{ name: string }>();

  const conv = await c.env.DB.prepare("SELECT * FROM Conversations WHERE Id = ?").bind(id).first<ConversationRow>();
  if (!conv) {
    return c.json({ error: `Conversation ${id} not found.` }, 404);
  }

  const name = body.name.trim();
  let tag = await c.env.DB.prepare("SELECT * FROM Tags WHERE CustomerId = ? AND Name = ?")
    .bind(conv.CustomerId, name)
    .first<TagRow>();

  if (!tag) {
    const result = await c.env.DB.prepare("INSERT INTO Tags (CustomerId, Name) VALUES (?, ?)")
      .bind(conv.CustomerId, name)
      .run();
    tag = { Id: result.meta.last_row_id, CustomerId: conv.CustomerId, Name: name };
  }

  const alreadyLinked = await c.env.DB.prepare(
    "SELECT 1 FROM ConversationTags WHERE ConversationsId = ? AND TagsId = ?",
  )
    .bind(id, tag.Id)
    .first();
  if (!alreadyLinked) {
    await c.env.DB.prepare("INSERT INTO ConversationTags (ConversationsId, TagsId) VALUES (?, ?)")
      .bind(id, tag.Id)
      .run();
  }

  return c.json(await loadConversationDto(c.env.DB, id));
});

/** Port of ConversationsController.RemoveTag — tag row itself is left in place for reuse. */
app.delete("/:id/tags/:tagId", async (c) => {
  const id = Number(c.req.param("id"));
  const tagId = Number(c.req.param("tagId"));

  const conv = await c.env.DB.prepare("SELECT Id FROM Conversations WHERE Id = ?").bind(id).first();
  if (!conv) {
    return c.json({ error: `Conversation ${id} not found.` }, 404);
  }

  await c.env.DB.prepare("DELETE FROM ConversationTags WHERE ConversationsId = ? AND TagsId = ?")
    .bind(id, tagId)
    .run();

  return c.json(await loadConversationDto(c.env.DB, id));
});

/** Port of ConversationsController.GetTags — a customer's own tags only. */
app.get("/tags", async (c) => {
  const customerId = Number(c.req.query("customerId") ?? "0");
  if (customerId <= 0) {
    return c.json({ error: "customerId query parameter is required." }, 400);
  }

  const { results } = await c.env.DB.prepare("SELECT * FROM Tags WHERE CustomerId = ? ORDER BY Name")
    .bind(customerId)
    .all<TagRow>();
  return c.json(results.map(toTagDto));
});

/** Port of ConversationsController.CleanupQuotedChains — safe to re-run. */
app.post("/cleanup-quoted-chains", async (c) => {
  const { results: messages } = await c.env.DB.prepare(
    "SELECT * FROM CommunicationLogs WHERE Body IS NOT NULL AND Body != ''",
  ).all<CommunicationLogRow>();

  let changed = 0;
  let charsRemoved = 0;

  for (const m of messages) {
    const trimmed = trimQuotedReplyChain(m.Body!);
    if (trimmed.length < m.Body!.length) {
      charsRemoved += m.Body!.length - trimmed.length;
      changed++;
      await c.env.DB.prepare("UPDATE CommunicationLogs SET Body = ? WHERE Id = ?").bind(trimmed, m.Id).run();
    }
  }

  return c.json({ totalMessagesScanned: messages.length, messagesChanged: changed, charactersRemoved: charsRemoved });
});

export default app;
