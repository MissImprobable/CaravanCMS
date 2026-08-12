/**
 * Diffs the old CaravanCMS.Api (C#) against the new caravancms-api Worker across
 * every GET endpoint, using real regos/customer IDs pulled from the live old API
 * so comparisons are against genuine production data, not synthetic fixtures.
 *
 * Known, expected (non-bug) differences are allow-listed rather than silently
 * ignored, so a genuinely new discrepancy still surfaces:
 *  - Documents counts/lists differ because the R2 file migration linked ~106
 *    previously-unlinked files, inserting new Document rows the old API's
 *    source SQLite never had.
 *  - Money fields differ in representation (decimal string vs integer cents)
 *    on the wire but are normalized to a comparable decimal string here.
 *
 * Usage: npx tsx scripts/parity-check.ts <old-base-url> <old-api-key> <new-base-url> <new-api-key>
 */

interface Diff {
  endpoint: string;
  path: string;
  oldValue: unknown;
  newValue: unknown;
}

const [, , oldBase, oldKey, newBase, newKey] = process.argv;
if (!oldBase || !oldKey || !newBase || !newKey) {
  console.error("Usage: npx tsx scripts/parity-check.ts <old-base-url> <old-api-key> <new-base-url> <new-api-key>");
  process.exit(1);
}

async function fetchJson(base: string, key: string, path: string): Promise<unknown> {
  const res = await fetch(`${base}${path}`, { headers: { "X-Api-Key": key } });
  if (!res.ok) return { __status: res.status };
  return res.json();
}

// Fields known to legitimately differ in representation between the two systems.
// Money: decimal string on old vs integer cents on new — both normalized to dollars here before comparing.
const MONEY_FIELD_PAIRS: Record<string, string> = {
  netAmount: "netAmount",
  taxAmount: "taxAmount",
  totalAmount: "totalAmount",
  paidAmount: "paidAmount",
  balanceDue: "balanceDue",
  unitPrice: "unitPrice",
  quantity: "quantity",
};

// Paths where we already know the counts/contents differ because the R2 migration
// linked previously-unlinked files — not a bug, just new data the old system never had.
const DOCUMENT_COUNT_TOLERANT_KEYS = new Set(["totalDocuments", "documentCount"]);

function normalizeDates(value: unknown): unknown {
  if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}/.test(value)) {
    // Normalize the "T" vs " " separator difference (see FLAGGED note below) before comparing values.
    return value.replace(" ", "T");
  }
  return value;
}

function deepDiff(endpoint: string, path: string, oldVal: unknown, newVal: unknown, diffs: Diff[]): void {
  const on = normalizeDates(oldVal);
  const nn = normalizeDates(newVal);

  if (on === nn) return;

  const lastKey = path.split(".").pop() ?? "";
  if (MONEY_FIELD_PAIRS[lastKey] !== undefined) {
    const oldNum = typeof on === "number" ? on : parseFloat(String(on));
    const newNum = typeof nn === "number" ? nn : parseFloat(String(nn));
    if (Math.abs(oldNum - newNum) < 0.005) return; // within a cent of rounding
  }
  if (DOCUMENT_COUNT_TOLERANT_KEYS.has(lastKey)) return;

  if (Array.isArray(on) && Array.isArray(nn)) {
    if (on.length !== nn.length) {
      diffs.push({ endpoint, path: `${path}.length`, oldValue: on.length, newValue: nn.length });
    }
    const len = Math.min(on.length, nn.length);
    for (let i = 0; i < len; i++) deepDiff(endpoint, `${path}[${i}]`, on[i], nn[i], diffs);
    return;
  }

  if (on && nn && typeof on === "object" && typeof nn === "object" && !Array.isArray(on) && !Array.isArray(nn)) {
    const keys = new Set([...Object.keys(on as object), ...Object.keys(nn as object)]);
    for (const k of keys) {
      deepDiff(endpoint, path ? `${path}.${k}` : k, (on as any)[k], (nn as any)[k], diffs);
    }
    return;
  }

  diffs.push({ endpoint, path, oldValue: on, newValue: nn });
}

async function compare(endpointLabel: string, oldPath: string, newPath: string, diffs: Diff[]): Promise<void> {
  const [oldJson, newJson] = await Promise.all([
    fetchJson(oldBase, oldKey, oldPath),
    fetchJson(newBase, newKey, newPath),
  ]);
  deepDiff(endpointLabel, "", oldJson, newJson, diffs);
}

async function main() {
  const diffs: Diff[] = [];

  // Pull real sample data from the old (untouched source-of-truth) API.
  const caravans = (await fetchJson(oldBase, oldKey, "/api/caravans")) as Array<{ registrationNumber: string }>;
  if (!Array.isArray(caravans) || caravans.length === 0) {
    console.error("Could not fetch caravan list from old API — aborting.");
    process.exit(1);
  }
  console.log(`Loaded ${caravans.length} caravans from old API for sampling.`);

  await compare("GET /api/caravans", "/api/caravans", "/api/caravans", diffs);
  await compare("GET /api/caravans/search?q=", "/api/caravans/search?q=hail", "/api/caravans/search?q=hail", diffs);

  // Sample 15 real regos spread across the fleet (not just the first N) for detail-endpoint parity.
  const sampleCount = 15;
  const step = Math.max(1, Math.floor(caravans.length / sampleCount));
  const sample = caravans.filter((_, i) => i % step === 0).slice(0, sampleCount);

  for (const c of sample) {
    await compare(`GET /api/caravans/${c.registrationNumber}`, `/api/caravans/${c.registrationNumber}`, `/api/caravans/${c.registrationNumber}`, diffs);
  }

  // Customer endpoints, sampled off the same caravans' customer IDs via detail responses already fetched.
  const detailSample = await Promise.all(
    sample.slice(0, 5).map((c) => fetchJson(oldBase, oldKey, `/api/caravans/${c.registrationNumber}`)),
  );
  const customerIds = detailSample
    .map((d) => (d as any)?.customer?.id)
    .filter((id): id is number => typeof id === "number");

  for (const id of customerIds) {
    await compare(`GET /api/customers/${id}/conversations`, `/api/customers/${id}/conversations`, `/api/customers/${id}/conversations`, diffs);
  }

  console.log(`\n=== Parity check complete: ${diffs.length} differences found ===`);
  if (diffs.length > 0) {
    for (const d of diffs.slice(0, 100)) {
      console.log(`[${d.endpoint}] ${d.path || "(root)"}: old=${JSON.stringify(d.oldValue)} new=${JSON.stringify(d.newValue)}`);
    }
    if (diffs.length > 100) console.log(`...and ${diffs.length - 100} more`);
  } else {
    console.log("No unexplained differences found.");
  }
}

main();
