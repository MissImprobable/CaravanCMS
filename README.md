# CaravanCMS

Caravan service history management system for Caravanland. Centralises MechanicDesk data, links documents from your existing folder structure, and provides a clean interface for technicians.

## Architecture

```
CaravanCMS.Core        → Shared models and DTOs (no dependencies)
CaravanCMS.Api         → ASP.NET Core 9 REST API + SQLite database
CaravanCMS.Admin       → WPF app for importing and file scanning (server machine)
CaravanCMS.Client      → WPF app for viewing caravan history (all machines)
```

All apps communicate with the API over HTTP. The API is the single source of truth — no direct database access from WPF apps.

## Prerequisites

- .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9
- Visual Studio 2022 v17.8+ (or VS Code with C# extension)
- Windows 10/11 (WPF apps are Windows-only)

## Quick Setup

```powershell
# 1. Restore packages
dotnet restore

# 2. Start the API (creates caravan-cms.db automatically)
cd CaravanCMS.Api
dotnet run

# 3. Verify at http://localhost:5000/swagger
```

The database file `caravan-cms.db` is created automatically in the API's working directory on first run.

## Configuration

Edit `CaravanCMS.Api/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=caravan-cms.db"
  },
  "CaravanCMS": {
    "ApiKey": "your-secret-key-here",
    "CaravanHistoryPath": "C:\\Users\\info\\OneDrive - Caravanland\\Documents\\Caravan History",
    "MaxUploadSizeMB": 100
  }
}
```

> **Important:** Change `ApiKey` from the default before deploying. The same key must be entered in the Admin and Client app settings.

## API Authentication

All endpoints require the `X-API-Key` header:

```
X-API-Key: your-secret-key-here
```

Returns `401 Unauthorized` if missing or incorrect. Swagger UI is excluded from authentication.

## Key Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/caravans` | All caravans (summary) |
| GET | `/api/caravans/{id}` | Full detail with history |
| GET | `/api/caravans/search?q=term` | Fuzzy search |
| GET | `/api/caravans/stats` | Dashboard stats |
| POST | `/api/import/mechanicdesk` | Upload MechanicDesk Excel |
| POST | `/api/import/scan-files` | Scan folder for documents |
| POST | `/api/import/link-document` | Link file to caravan |
| GET | `/api/documents/{id}/download` | Stream file to client |

Full documentation at `http://localhost:5000/swagger`.

## MechanicDesk Import

The importer reads the first sheet (or one named "Jobs") and auto-detects column names. It handles:

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

Files with ≥50% confidence are auto-selected for linking. Review and override before confirming.

## EPPlus Licensing

This project uses EPPlus for Excel parsing, configured as `LicenseContext.NonCommercial`. For a business deployment, you should purchase a commercial license at https://epplussoftware.com/en/LicenseOverview.

## Logging

Logs are written to:
- Console (coloured, timestamped)
- `logs/caravan-cms-YYYY-MM-DD.log` (rolling daily, 30 days retained)

EF Core SQL queries are logged at Debug level (Development only).

## Database Migrations

The database is created automatically via `EnsureCreated()` on first run. To add migrations for schema changes later:

```powershell
cd CaravanCMS.Api
dotnet ef migrations add YourMigrationName
dotnet ef database update
```

## Project Structure

```
CaravanCMS/
├── CaravanCMS.Core/
│   └── Models.cs                  # All entities and DTOs
├── CaravanCMS.Api/
│   ├── Data/ApplicationDbContext.cs
│   ├── Controllers/
│   │   ├── CaravansController.cs
│   │   ├── JobsController.cs
│   │   ├── DocumentsController.cs
│   │   └── ImportController.cs
│   ├── Services/
│   │   ├── ExcelImportService.cs  # MechanicDesk Excel parser
│   │   ├── FileScanner.cs         # Folder walk + document linking
│   │   └── FuzzyMatcher.cs        # Caravan-to-file matching
│   └── Middleware/ApiKeyMiddleware.cs
├── CaravanCMS.Admin/
│   ├── Views/                     # Import, Scan, Settings dialogs
│   ├── ViewModels/                # MVVM logic
│   └── Services/                  # ApiClient, SettingsService
└── CaravanCMS.Client/
    ├── Views/                     # CaravanDetail (Info/Jobs/Docs tabs)
    ├── ViewModels/
    └── Services/                  # ApiClient, SettingsService
```
