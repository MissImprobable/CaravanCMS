export interface ImportConflict {
  entityType: string;
  mechanicDeskId: string;
  existingEntityId: number;
  existingDescription: string;
  incomingDescription: string;
  changedFields: Record<string, [string, string]>;
}

export interface ImportResult {
  customersImported: number;
  customersUpdated: number;
  caravansImported: number;
  caravansUpdated: number;
  jobsImported: number;
  jobsUpdated: number;
  invoicesImported: number;
  conflicts: ImportConflict[];
  errors: string[];
  warnings: string[];
  durationMs: number;
}

export function newImportResult(): ImportResult {
  return {
    customersImported: 0,
    customersUpdated: 0,
    caravansImported: 0,
    caravansUpdated: 0,
    jobsImported: 0,
    jobsUpdated: 0,
    invoicesImported: 0,
    conflicts: [],
    errors: [],
    warnings: [],
    durationMs: 0,
  };
}
