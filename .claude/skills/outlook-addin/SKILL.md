---
name: outlook-addin
description: How the CaravanCMS Outlook add-in actually works and deploys — architecture, cert setup, and the troubleshooting checklist for "the add-in doesn't show up in Outlook." Use whenever discussing the Outlook add-in, its manifest, deployment, or why it's not appearing in a ribbon.
---

# CaravanCMS Outlook Add-in — how it actually works

## Architecture

- `CaravanCMS.Api` is a self-hosted ASP.NET app that runs as a **WinForms system
  tray app on a single office PC** (`Program.cs`, `System.Windows.Forms.Application.Run`
  at the end of `Main`) — it is not a cloud service.
- The Outlook add-in manifest is **not a static file**. `GET /addin/manifest.xml`
  (`Program.cs` ~line 155) renders `wwwroot/addin/manifest.template.xml` at request
  time, substituting `{{BASE_URL}}` from config key `CaravanCMS:PublicBaseUrl`.
  If that config value is empty, the endpoint returns an HTTP 500 `Problem`
  response instead of XML.
- `appsettings.json` in source control ships with `PublicBaseUrl: ""` on purpose.
  The real value lives in `CaravanCMS.Api/appsettings.Production.json` — **this
  file does exist in the repo** (previously assumed to live outside it; it
  doesn't). It holds `PublicBaseUrl`, the Kestrel HTTPS binding, and the PFX
  certificate password in plain text. Read it before asking the user for any
  of those values — don't guess or ask when it's sitting right there.
- **The server PC is `192.168.1.150`.** This is a single-machine setup: the
  same PC that runs `CaravanCMS.Api.exe` (the tray app / Kestrel host) is also
  used as an Outlook client and hits its own add-in over the LAN address, not
  `localhost`. Check `Get-Process -Name CaravanCMS.Api` / `CaravanCMS.Admin`
  before assuming you're on a bystander machine — you may be on the server.
- Task pane UI: `wwwroot/addin/taskpane.html` / `.js` / `.css`. Only shows for
  read Message items (`FormType="Read"` rule in the manifest) — it will never
  appear on Compose, Calendar, or the inbox list with nothing open.

## TLS / trust model

- `certs/New-CaravanCmsCertificate.ps1` issues a **self-signed cert** bound to
  the server PC's static LAN address/hostname (not a public CA cert — despite
  looking "signed," it's self-issued and must be explicitly trusted).
- `certs/Install-CaravanCmsTrust.ps1` must be run **as Administrator on every
  client PC** to install `caravancms.cer` into that machine's Trusted Root store.
  Skipping this causes cert-untrusted errors when Outlook (or a browser) hits
  the manifest URL.
- The `-Address` used at cert creation time must exactly match what's later put
  in `PublicBaseUrl` (`https://<address>:61655`) — a mismatch (e.g. cert issued
  for an IP but `PublicBaseUrl` uses a hostname) breaks TLS validation even
  though the cert is trusted.
- **IP-address SAN bug (fixed 2026-08-11):** `New-SelfSignedCertificate -DnsName
  <ip>` puts the IP into a `DNS Name` SAN entry, not an `IP Address` SAN entry.
  Chromium-based renderers — which includes **WebView2, the engine Outlook's
  task pane runs in** — flatly reject that when connecting to a literal IP,
  producing exactly the error "content is blocked because it isn't signed by
  a valid security certificate," even when the cert is fully trusted in
  `LocalMachine\Root`. A plain browser tab (e.g. Safari) can be more lenient
  and appear to load fine, which is a red herring — it doesn't mean the cert
  is actually valid for Outlook's renderer.
  `New-CaravanCmsCertificate.ps1` now detects an IP `-Address` and builds the
  SAN explicitly via `-TextExtension "2.5.29.17={text}IPAddress=<ip>"` instead
  of relying on `-DnsName`. Verify any cert with:
  `certutil -dump certs\caravancms.cer | Select-String "Subject Alternative Name" -Context 0,2`
  — must say `IP Address=`, not `DNS Name=`. If you ever see the latter, the
  cert needs regenerating with the fixed script, not just re-trusting.
- **Elevation from an automated/non-interactive shell doesn't work reliably
  here.** `Start-Process -Verb RunAs` was tried repeatedly from this tool's
  PowerShell to install cert trust — it returns with no error and no effect,
  because there's no interactive desktop session for UAC to prompt on. Cert
  trust installation (`Install-CaravanCmsTrust.ps1 -Scope LocalMachine`)
  needs the user to run it themselves in a real elevated PowerShell window.
  Don't loop on trying to automate this — ask once, clearly, and wait.

## Deployment — sideloading is NOT available to end users anymore

Microsoft has deprecated manual sideloading ("Add from URL" / "Add from File")
for regular (non-admin) users in modern tenants. **Do not suggest sideloading
as the fix** — it was already ruled out. The only viable path for this
single-server, LAN-only setup is **Microsoft 365 admin center → Integrated
apps**, uploading/pointing at `https://<server-address>:61655/addin/manifest.xml`
and deploying centrally to the relevant users.

## Verifying the manifest is actually valid (don't just eyeball a browser tab)

Browsers render add-in XML with tags stripped when there's no stylesheet — a
page that looks like garbled text (`250 ReadItem false ...`) is actually the
manifest rendering *correctly*: those are `RequestedHeight`, `Permissions`,
and `DisableEntityHighlighting` values with markup stripped, not an error.
Don't mistake that for a broken manifest. `curl -k https://<address>:61655/addin/manifest.xml`
from a client machine is a cleaner check.

## Troubleshooting checklist, in order, when "the add-in is enabled but doesn't
## show up in Outlook"

1. **Manifest reachable from a client PC (not the server)?** Hit the manifest
   URL from a browser or `curl` on an actual client machine. Cert warning →
   client hasn't run `Install-CaravanCmsTrust.ps1`, or `-Address` mismatch.
2. **Admin center deployment status** — Integrated apps → the app → must say
   "Deployed" (not "Uploading"/processing), and be assigned to the right
   users/group.
3. **Assignment mode: Fixed vs. Available.** This is the easy-to-miss one.
   - *Fixed* = pushed automatically, appears in the ribbon without user action.
   - *Available* (a.k.a. "Optional/User can turn on") = the add-in only shows
     up under `Home → Get Add-ins → My add-ins → Admin-managed`, and each user
     must manually click to add it. If deployment was set to Available, "it's
     enabled but I don't see it" is expected — check that dialog before
     assuming anything is broken.
4. **Mailbox type.** Centralized deployment via admin center only reaches
   Exchange Online mailboxes with modern auth. An on-prem/hybrid mailbox
   without modern auth will silently never receive it, regardless of admin
   center status.
5. **Outlook needs a real relaunch**, not just closing the window — quit fully
   (check it's not still in the system tray/background) and reopen. First
   propagation to a client can take a while; if items 1–4 all check out and
   it still isn't there, that's a wait-and-recheck situation, not a config bug.
6. **Open an actual email to read it.** The ribbon button only renders via
   `MessageReadCommandSurface` on a read `Message` item — nothing shows on the
   inbox list, Calendar, or Compose.
7. **"All apps" listing with no icon and a generic "Start using X, it works
   when you are reading your emails" message, and clicking Open does nothing**
   — this is normal, not a bug. It's Outlook's stock description for any
   contextual (`FormType="Read"`) add-in, shown because there's nothing to
   activate from that list. Go find the button on the **Home ribbon tab**
   while actually reading an email instead.
8. **Ribbon button present, clicking it gives "Add-In Error... content is
   blocked because it isn't signed by a valid security certificate"** — check,
   in this order: (a) is the cert trusted in `Cert:\LocalMachine\Root` on
   *this specific PC* (`Get-ChildItem Cert:\LocalMachine\Root | Where
   FriendlyName -like "*CaravanCMS*"` — empty result means trust was never
   actually installed here, regardless of what anyone assumed); (b) does the
   SAN say `IP Address=` and not `DNS Name=` per the cert bug above.

## Related Admin-app bug (fixed 2026-08-11)

`CaravanCMS.Admin`'s `ApiHostService` (the thing that starts/stops
`CaravanCMS.Api.exe`) used to be built once at Admin startup with whatever
`ApiExePath` was in settings *at that moment*, and never refreshed. Changing
the exe path in Settings and clicking Save persisted it to disk fine, but the
already-running `ApiHost` kept using the stale path — so clicking "Start API"
straight after saving a corrected path still failed with "Could not find
CaravanCMS.Api.exe." Fixed via `App.RefreshApiHostSettings()`, called from
`SettingsWindow.Save_Click`. If this resurfaces, check whether `App.ApiHost`
is being rebuilt after a settings save, not just whether the path itself is
correct.

## Build output holds its own stale copy of certs — don't edit-and-assume

`certs/*.pfx`/`*.cer` living under `CaravanCMS.Api/` are **copied into
`bin/<Config>/<TFM>/certs/` at build time** and the running process reads the
copy next to the exe, not the source-tree original. Regenerating the cert in
`CaravanCMS.Api/certs/` does nothing for an already-built, already-running
instance until that copy is also refreshed (rebuild, or manually copy the
new `.pfx`/`.cer` into the `bin/...` folder) **and the process is restarted**.
Symptom if you skip this: everything looks fixed on paper (trust installed,
SAN correct in the source file) but Outlook/Edge still throws
`net::ERR_CERT_COMMON_NAME_INVALID` against the *old* cert, because that's
still what Kestrel is actually presenting on the wire. To verify what's
really being served, don't just inspect files — hit the live endpoint through
the actual renderer:
```powershell
msedge --headless=new --disable-gpu --no-sandbox --user-data-dir=<tmp dir> `
  --virtual-time-budget=8000 --dump-dom https://<address>:61655/addin/taskpane.html
```
`<title>Privacy error</title>` + `net::ERR_CERT_...` in the output means the
live cert is still bad; `<title>CaravanCMS</title>` means it's genuinely
fixed. This is more trustworthy than `Invoke-WebRequest`/`curl`, which don't
replicate Chromium's (and therefore WebView2's/Outlook's) cert validation.

## Resolved end-to-end (2026-08-11)

Full chain that blocked the add-in, in the order each layer was found:
1. Cert trust never installed on the server/client PC (`LocalMachine\Root`
   empty).
2. Cert had a `DNS Name` SAN for an IP address instead of `IP Address` SAN —
   rejected by WebView2 regardless of trust. Fixed in
   `New-CaravanCmsCertificate.ps1`.
3. Even after regenerating the cert, the **build-output copy** under
   `bin\Debug\net10.0-windows\certs\` was untouched and the running process
   kept serving the old one (see section above) — the actual last blocker.
4. Fixed by copying the regenerated `.pfx`/`.cer` into the `bin\...\certs\`
   folder and restarting `CaravanCMS.Api.exe`. Confirmed via headless Edge
   that the live endpoint now serves a cert Chromium accepts, and the user
   confirmed the add-in successfully round-tripped a request to the server
   from within Outlook.
- Server PC is `192.168.1.150`, running `CaravanCMS.Api.exe` as the Kestrel
  host; this same PC is also used as an Outlook client.
- Admin center: Deployed, assigned to everyone. Add-in became visible via
  Outlook's "All apps" list and was added to the ribbon from there.
- **Still outstanding:** the fresh `caravancms.cer` needs distributing to any
  *other* office PCs, and `Install-CaravanCmsTrust.ps1 -Scope LocalMachine`
  re-run on each of them — this session only fixed the server/first client.
- Also fixed in passing: stale `net9.0`/`net9.0-windows` build output across
  all four projects (leftover from a prior TargetFramework upgrade — every
  `.csproj` now targets `net10.0`/`net10.0-windows` only) plus a stray
  non-windows `net10.0` build of `CaravanCMS.Api`. All safe to delete
  (gitignored, regenerated by `dotnet build`); worth a periodic sanity check
  since a stale duplicate build folder was the direct cause of item 3 above.
