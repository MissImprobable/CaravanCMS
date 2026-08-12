# CaravanCMS

Caravan service history management system for Caravanland. Centralises MechanicDesk data, links documents from your existing folder structure, and provides a clean interface for technicians.

## Architecture

```
CaravanCMS.Core        → Shared models and DTOs (no dependencies)
CaravanCMS.Worker      → Cloudflare Worker (Hono) REST API + D1 database + R2 document storage
CaravanCMS.Admin       → WPF app for importing and file scanning
CaravanCMS.Client      → WPF app for viewing caravan history (all machines)
```

All apps communicate with the API over HTTPS at `https://api.caravanland.co.nz`. The Worker is the single source of truth — no direct database access from WPF apps. The Outlook add-in (task pane + manifest) is also served by the Worker, from `CaravanCMS.Worker/public/addin/`.

> The original backend was a self-hosted ASP.NET Core app (`CaravanCMS.Api`) running as a Windows tray app on an office PC. It has been decommissioned and removed — everything now runs on Cloudflare.

## Prerequisites

- Node.js 20+ and npm
- A Cloudflare account with access to the `caravancms-api` Worker, `caravancms` D1 database, and `caravancms-documents` R2 bucket
- .NET 10 SDK (for the Admin/Client WPF apps): https://dotnet.microsoft.com/download/dotnet/10
- Visual Studio 2022 v17.12+ (or VS Code with C# extension)
- Windows 10/11 (the WPF apps are Windows-only; the Worker itself is platform-independent)

## Quick Setup — Worker (backend)

```powershell
cd CaravanCMS.Worker
npm install

# Local dev server (uses --local D1/R2 unless configured otherwise)
npm run dev

# Deploy to Cloudflare
npm run deploy

# Apply D1 migrations
npm run d1:migrate:remote   # or d1:migrate:local for local dev
```

Configuration lives in `CaravanCMS.Worker/wrangler.toml`:

```toml
[vars]
PUBLIC_BASE_URL = "https://api.caravanland.co.nz"   # used for the add-in manifest's {{BASE_URL}}
```

`API_KEY` is a secret, not a plain var — set it with:

```powershell
wrangler secret put API_KEY
```

## Quick Setup — WPF apps

```powershell
dotnet restore
```

Open `CaravanCMS.Client` or `CaravanCMS.Admin` settings and enter the server URL (`https://api.caravanland.co.nz`) and the API key. Both apps' `ApiClient.cs` set `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` on their JSON options — required because the Worker does plain case-sensitive JSON parsing (see the `outlook-addin` skill for more on this gotcha).

## API Authentication

All `/api/*` endpoints require the `X-API-Key` header:

```
X-API-Key: your-secret-key-here
```

Returns `401 Unauthorized` if missing or incorrect. `/health` and `/addin/*` are unauthenticated.

## Key Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/caravans` | All caravans (summary) |
| GET | `/api/caravans/{rego}` | Full detail with history (keyed by registration number) |
| GET | `/api/caravans/search?q=term` | Search by VIN, rego, make/model, or customer name |
| GET | `/api/caravans/stats` | Dashboard stats |
| GET | `/api/customers/lookup?email=` | Customer match by email (used by the Outlook add-in) |
| POST | `/api/conversations` | Start/append to a conversation thread (idempotent by `externalConversationId`) |
| POST | `/api/conversations/{id}/messages` | Log a message against a conversation |
| POST | `/api/conversations/{id}/tags` | Attach a label to a conversation |
| POST | `/api/import/mechanicdesk` | Upload MechanicDesk Excel (.xlsx only) |
| POST | `/api/import/scan-files` | Scan folder for documents |
| POST | `/api/documents` | Upload a document (multipart), stored in R2 |
| GET | `/api/documents/{id}/download` | Stream file from R2 |
| GET | `/health` | Health check (unauthenticated) |
| GET | `/addin/manifest.xml` | Outlook add-in manifest (unauthenticated, renders `manifest.template.xml`) |

## MechanicDesk Import

The importer reads the first sheet (or one named "Jobs") and auto-detects column names. Only `.xlsx` files are accepted — re-save `.xls` files from Excel first. It handles:

- **Deduplication:** Uses `MechanicDeskId` to safely re-import the same file multiple times without duplicating data
- **Conflicts:** Records where existing data differs from the import are flagged for review
- **Adaptive columns:** Finds columns by common name variants (e.g. "Rego", "Registration", "Reg Number")

## File Scanning & Linking

The scanner walks the Caravan History folder and matches files to caravans using:

| Priority | Method | Confidence |
|----------|--------|-----------|
| 1 | VIN found in filename | 95% |
| 2 | Registration found in filename | 88% |
| 3 | VIN/reg found in folder path | 72% |
| 4 | Fuzzy make/model match | 35–60% |

Files with ≥50% confidence are auto-selected for linking. Review and override before confirming. Matched files are uploaded into R2 via `POST /api/documents`, resized in-Worker for images (see `CaravanCMS.Worker/src/lib/imageResize.ts`).

## Outlook Add-in

Task pane + manifest live in `CaravanCMS.Worker/public/addin/` and are served directly by the Worker's static assets binding, plus the templated `/addin/manifest.xml` route. See the `outlook-addin` Claude Code skill (`.claude/skills/outlook-addin/`) for deployment (Microsoft 365 admin center → Integrated apps) and troubleshooting.

## Project Structure

```
CaravanCMS/
├── CaravanCMS.Core/
│   └── Models.cs                  # All entities and DTOs
├── CaravanCMS.Worker/
│   ├── src/
│   │   ├── index.ts               # Route mounting, manifest.xml, health check
│   │   ├── routes/                # caravans, customers, conversations, documents, jobs, import
│   │   ├── lib/                   # mappers, fuzzy matching, image resize, reply-chain trimming
│   │   └── middleware/apiKey.ts
│   ├── public/addin/              # Outlook add-in task pane (html/js/css) + manifest template
│   ├── migrations/                # D1 schema migrations
│   └── scripts/                   # one-off migration/import scripts
├── CaravanCMS.Admin/
│   ├── Views/                     # Import, Scan, Settings dialogs
│   ├── ViewModels/                # MVVM logic
│   └── Services/                  # ApiClient, SettingsService
└── CaravanCMS.Client/
    ├── Views/                     # CaravanDetail (Info/Jobs/Documents/Conversations tabs)
    ├── ViewModels/
    └── Services/                  # ApiClient, SettingsService
```
