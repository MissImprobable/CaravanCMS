import { Hono } from "hono";
import type { Env } from "../env";
import { importMechanicDeskExcel } from "../import/importer";

const app = new Hono<{ Bindings: Env }>();

/**
 * Port of ImportController.ImportMechanicDesk. Kept synchronous rather than
 * the originally-planned R2-upload + Workflow design: verified against the
 * real 2.1MB production MechanicDesk export (984 customers, 590 caravans,
 * 1,059 jobs, 1,042 invoices), which completed in ~24s well within Workers'
 * CPU-time budget (most of that time is I/O-bound D1 round-trips, which
 * don't count against CPU time). If a genuinely much larger export (tens of
 * MB) ever hits real production CPU/time limits, revisit with the
 * R2-upload + Workflow design documented in the migration plan — but don't
 * build that speculatively against a risk that hasn't materialized.
 */
app.post("/mechanicdesk", async (c) => {
  const form = await c.req.formData();
  const file = form.get("file");
  if (!(file instanceof File)) {
    return c.json({ error: "No file uploaded." }, 400);
  }
  if (!file.name.toLowerCase().endsWith(".xlsx")) {
    return c.json(
      { error: "File must be a .xlsx Excel file. Legacy .xls format is not supported — please re-save the file as .xlsx from Excel first." },
      400,
    );
  }

  const buffer = await file.arrayBuffer();
  const result = await importMechanicDeskExcel(c.env.DB, buffer);
  return c.json(result);
});

export default app;
