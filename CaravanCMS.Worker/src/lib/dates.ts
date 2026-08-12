/**
 * D1/SQLite stores datetimes as TEXT in the space-separated form EF Core originally wrote them in
 * (e.g. "2026-05-25 01:02:36.9138292") — valid SQLite, but not valid ISO-8601. The old C# API never
 * hit this because EF Core parsed the column into a real DateTime before System.Text.Json serialized
 * it back out with a "T" separator; the Worker passes D1's raw TEXT straight through, which .NET's
 * strict DateTime JSON converter on the client side then rejects. Swapping the separator is the
 * complete fix — the value itself (including sub-second precision) is otherwise already correct.
 */
export function toIso(value: string | null | undefined): string | null {
  if (!value) return null;
  return value.includes(" ") ? value.replace(" ", "T") : value;
}
