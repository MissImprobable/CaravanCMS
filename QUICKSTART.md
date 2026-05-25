# CaravanCMS — 15-Minute Quick Start

Follow these steps in order. You'll have a working system by the end.

## Step 1 — Install Prerequisites (5 min)

1. Install **.NET 9 SDK** from https://dotnet.microsoft.com/download/dotnet/9
2. Verify: open a terminal and run `dotnet --version` (should show 9.x.x)

## Step 2 — Start the API (2 min)

Open **PowerShell** or **Command Prompt** and run:

```powershell
cd "C:\Users\info\source\repos\CaravanCMS\CaravanCMS.Api"
dotnet run
```

You should see:
```
[HH:mm:ss INF] Database ready at: caravan-cms.db
[HH:mm:ss INF] CaravanCMS API listening. Swagger UI: http://localhost:5000/swagger
```

> **Keep this terminal open** — the API must be running for the WPF apps to work.

## Step 3 — Verify the API works (1 min)

Open your browser and go to: **http://localhost:5000/swagger**

You should see the Swagger UI with all endpoints listed.

Try: click **GET /api/caravans/stats → Try it out → Execute**

Enter the API key `caravanland-internal-api-key-2024` in the Authorize button first.

## Step 4 — Launch the Admin App (2 min)

Open a **new** terminal:

```powershell
cd "C:\Users\info\source\repos\CaravanCMS\CaravanCMS.Admin"
dotnet run
```

Or open `CaravanCMS.sln` in Visual Studio and run `CaravanCMS.Admin`.

On first launch:
1. Click **⚙ Settings**
2. Confirm API Endpoint is `http://localhost:5000`
3. Enter API Key: `caravanland-internal-api-key-2024`
4. Set your Caravan History folder path
5. Click **Test Connection** → should show ✅
6. Click **Save Settings**

## Step 5 — Import MechanicDesk Data (3 min)

1. In the Admin dashboard, click **Browse & Import Excel File**
2. Select your MechanicDesk Excel export (`.xlsx` or `.xls`)
3. Click **Start Import**
4. Review results — customers, caravans, and jobs will be imported
5. The stats on the dashboard will update automatically

## Step 6 — Scan and Link Documents (2 min)

1. Click **Scan Caravan History Folder**
2. Click **Start Scan** (uses your configured folder path)
3. Review the matched files — high-confidence matches are pre-selected
4. Override any incorrect matches using the Confidence column
5. Click **Link Selected Documents**

## Step 7 — Launch the Client App (1 min)

On any machine on your network:

```powershell
cd "C:\Users\info\source\repos\CaravanCMS\CaravanCMS.Client"
dotnet run
```

> **Network setup:** If running on a different machine, edit `client-settings.json` in `%LOCALAPPDATA%\CaravanCMS\` and change `ApiBaseUrl` to `http://SERVER-IP:5000`.

1. The search box searches by VIN, registration, make, model, or customer name
2. Press **Enter** or click **Search**
3. Double-click any row to open the full caravan history
4. Navigate the Info / Job History / Documents tabs
5. Click **Download** on any document to open it

## Default Settings Reference

| Setting | Default Value |
|---------|--------------|
| API Port | 5000 |
| API Key | `caravanland-internal-api-key-2024` |
| Database | `caravan-cms.db` (in API folder) |
| Log folder | `logs/` (in API folder) |
| Settings (Admin) | `%LOCALAPPDATA%\CaravanCMS\admin-settings.json` |
| Settings (Client) | `%LOCALAPPDATA%\CaravanCMS\client-settings.json` |

## Troubleshooting

**API won't start:**
- Check .NET 9 is installed: `dotnet --version`
- Check port 5000 isn't in use: `netstat -an | findstr 5000`
- Check the logs folder for error details

**"Invalid API key" errors:**
- Make sure the key in appsettings.json matches the key in the WPF app settings exactly

**Import finds no columns:**
- The Excel file must have column headers in row 1
- Common headers are auto-detected (Customer, Job Number, VIN, Rego, etc.)
- Check the Swagger endpoint for error details

**Documents don't download:**
- The file path must be accessible from the **server machine** (where the API runs)
- UNC paths like `\\server\share\file.pdf` work if the API process has access

**Client can't connect:**
- Ensure the API is running and the server machine's firewall allows port 5000
- On the server: `netsh advfirewall firewall add rule name="CaravanCMS" dir=in action=allow protocol=TCP localport=5000`
- Change API listening URL in `appsettings.json`: `"Urls": "http://0.0.0.0:5000"`

## Changing the API Key

1. Edit `CaravanCMS.Api/appsettings.json` — change `CaravanCMS:ApiKey`
2. Restart the API
3. Update the key in Admin and Client app Settings windows
