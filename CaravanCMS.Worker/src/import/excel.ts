import ExcelJS from "exceljs";

/** Port of ExcelImportService.BuildColumnMap — lowercased header -> 1-based column index. */
export function buildColumnMap(sheet: ExcelJS.Worksheet): Map<string, number> {
  const map = new Map<string, number>();
  const headerRow = sheet.getRow(1);
  const colCount = sheet.columnCount;

  for (let col = 1; col <= colCount; col++) {
    const raw = headerRow.getCell(col).text?.trim().toLowerCase();
    if (raw && !map.has(raw)) map.set(raw, col);
  }
  return map;
}

/** Port of ExcelImportService.GetCell — first non-empty match across candidate header synonyms. */
export function getCell(sheet: ExcelJS.Worksheet, row: number, cols: Map<string, number>, ...names: string[]): string | null {
  for (const name of names) {
    const col = cols.get(name);
    if (col === undefined) continue;
    const value = sheet.getRow(row).getCell(col).text?.trim();
    if (value) return value;
  }
  return null;
}

/** Port of ExcelImportService.FindSheet. */
export function findSheet(workbook: ExcelJS.Workbook, ...names: string[]): ExcelJS.Worksheet | null {
  for (const name of names) {
    const sheet = workbook.worksheets.find((s) => s.name.toLowerCase() === name.toLowerCase());
    if (sheet) return sheet;
  }
  return workbook.worksheets.find((s) => s.actualRowCount > 0) ?? null;
}

/** Row count of a sheet's used range (ExcelJS equivalent of EPPlus's Dimension.Rows). */
export function sheetRowCount(sheet: ExcelJS.Worksheet): number {
  return sheet.actualRowCount;
}

export function parseIntOrNull(value: string | null, min = -Infinity): number | null {
  if (!value) return null;
  const n = parseInt(value, 10);
  return Number.isFinite(n) && n > min ? n : null;
}

export function parseMoney(value: string | null): number {
  if (!value) return 0;
  const cleaned = value.replace(/[$,]/g, "");
  const n = parseFloat(cleaned);
  return Number.isFinite(n) ? n : 0;
}

export function parseDateIso(value: string | null): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}
