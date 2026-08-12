import { Hono } from "hono";
import type { Env } from "../env";
import type { CustomerRow, ConversationRow, TagRow, CommunicationLogRow } from "../lib/rows";
import { toCustomerLookupDto, toConversationDto } from "../lib/mappers";

const app = new Hono<{ Bindings: Env }>();

/**
 * Port of CustomersController.LookupByEmail — exact case-insensitive match,
 * the core lookup the Outlook add-in uses to decide if a sender is known.
 */
app.get("/lookup", async (c) => {
  const email = c.req.query("email");
  if (!email || !email.trim()) {
    return c.json({ error: "email query parameter is required." }, 400);
  }

  const normalized = email.trim().toUpperCase();
  const customer = await c.env.DB.prepare(
    "SELECT * FROM Customers WHERE Email IS NOT NULL AND UPPER(Email) = ?",
  )
    .bind(normalized)
    .first<CustomerRow>();

  if (!customer) {
    return c.json({ error: `No customer found with email ${email}.` }, 404);
  }

  const [caravanCount, jobCount] = await Promise.all([
    c.env.DB.prepare("SELECT COUNT(*) c FROM Caravans WHERE CustomerId = ?")
      .bind(customer.Id)
      .first<{ c: number }>(),
    c.env.DB.prepare("SELECT COUNT(*) c FROM Jobs WHERE CustomerId = ?")
      .bind(customer.Id)
      .first<{ c: number }>(),
  ]);

  return c.json(toCustomerLookupDto(customer, caravanCount?.c ?? 0, jobCount?.c ?? 0));
});

/** Port of CustomersController.Search — substring match, min 2 chars, top 50. */
app.get("/search", async (c) => {
  const q = c.req.query("q");
  if (!q || q.trim().length < 2) {
    return c.json({ error: "Search query must be at least 2 characters." }, 400);
  }

  const term = `%${q.trim().toUpperCase()}%`;
  const { results } = await c.env.DB.prepare(
    `SELECT * FROM Customers
     WHERE UPPER(Name) LIKE ?1 OR UPPER(Email) LIKE ?1 OR UPPER(Phone) LIKE ?1
        OR UPPER(Mobile) LIKE ?1 OR UPPER(CustomerNumber) LIKE ?1
     LIMIT 50`,
  )
    .bind(term)
    .all<CustomerRow>();

  const dtos = await Promise.all(
    results.map(async (customer) => {
      const [caravanCount, jobCount] = await Promise.all([
        c.env.DB.prepare("SELECT COUNT(*) c FROM Caravans WHERE CustomerId = ?")
          .bind(customer.Id)
          .first<{ c: number }>(),
        c.env.DB.prepare("SELECT COUNT(*) c FROM Jobs WHERE CustomerId = ?")
          .bind(customer.Id)
          .first<{ c: number }>(),
      ]);
      return toCustomerLookupDto(customer, caravanCount?.c ?? 0, jobCount?.c ?? 0);
    }),
  );

  return c.json(dtos);
});

/** Port of CustomersController.GetConversations — newest activity first. */
app.get("/:id/conversations", async (c) => {
  const id = Number(c.req.param("id"));

  const exists = await c.env.DB.prepare("SELECT 1 FROM Customers WHERE Id = ?").bind(id).first();
  if (!exists) {
    return c.json({ error: `Customer ${id} not found.` }, 404);
  }

  const { results: conversations } = await c.env.DB.prepare(
    "SELECT * FROM Conversations WHERE CustomerId = ? ORDER BY LastActivityAt DESC",
  )
    .bind(id)
    .all<ConversationRow>();

  const dtos = await Promise.all(
    conversations.map(async (conv) => {
      const [tags, messages] = await Promise.all([
        c.env.DB.prepare(
          `SELECT t.* FROM Tags t
           JOIN ConversationTags ct ON ct.TagsId = t.Id
           WHERE ct.ConversationsId = ?`,
        )
          .bind(conv.Id)
          .all<TagRow>(),
        c.env.DB.prepare("SELECT * FROM CommunicationLogs WHERE ConversationId = ?")
          .bind(conv.Id)
          .all<CommunicationLogRow>(),
      ]);
      return toConversationDto(conv, tags.results, messages.results);
    }),
  );

  return c.json(dtos);
});

export default app;
