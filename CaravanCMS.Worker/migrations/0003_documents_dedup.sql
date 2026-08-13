-- Hard backstop against the duplicate-Documents-row bug (see scripts/dedupe-documents.ts for the
-- one-off cleanup this followed). The POST /api/documents existence check is the primary guard,
-- but a DB-level constraint means a bug in that check (or a future code path that bypasses it)
-- can't silently reintroduce duplicates. Scoped to real source paths only (excludes the synthetic
-- "r2://REG/file.jpg" placeholder used when no sourceFilePath is available) since two unrelated
-- uploads with no known source path have no real identity to be unique on.
CREATE UNIQUE INDEX IF NOT EXISTS UX_Documents_Registration_FilePath
  ON Documents (RegistrationNumber, FilePath)
  WHERE FilePath NOT LIKE 'r2://%';
