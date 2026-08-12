import { Hono } from "hono";
import type { Env } from "../env";

const app = new Hono<{ Bindings: Env }>();

// TODO: port JobsController.cs ({id}, by-caravan/{rego}) — not currently called
// by any client (job data arrives embedded in caravan detail), low priority.

export default app;
