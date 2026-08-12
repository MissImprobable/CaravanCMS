---
name: outlook-addin
description: How the CaravanCMS Outlook add-in actually works and deploys — architecture and the troubleshooting checklist for "the add-in doesn't show up in Outlook." Use whenever discussing the Outlook add-in, its manifest, deployment, or why it's not appearing in a ribbon.
---

# CaravanCMS Outlook Add-in — how it actually works

## Architecture (current, post-migration to Cloudflare)

- The backend is **`CaravanCMS.Worker`**, a Cloudflare Worker (Hono), backed by
  D1 (database) and R2 (documents). It is deployed at `https://api.caravanland.co.nz`
  and serves both the API and the add-in's static assets. There is no local
  server, no tray app, and no self-signed cert to manage — Cloudflare terminates
  TLS with a normal publicly-trusted certificate.
- **`CaravanCMS.Api` (the old self-hosted ASP.NET tray app) was decommissioned
  and removed from the repo/solution.** If you find historical references to
  it (docs, memory, old session notes), treat them as superseded — the add-in,
  Client, and Admin apps all point at the Worker now.
- The manifest is **not a static file**: `GET /addin/manifest.xml`
  (`CaravanCMS.Worker/src/index.ts` ~line 28) renders
  `CaravanCMS.Worker/src/lib/manifest.template.xml` at request time,
  substituting `{{BASE_URL}}` from the `PUBLIC_BASE_URL` environment variable
  (set in `wrangler.toml`, currently `https://api.caravanland.co.nz`).
- Task pane UI: `CaravanCMS.Worker/public/addin/taskpane.html` / `.js` / `.css`
  — plain static assets served directly by the Workers assets binding, no
  route needed for them. Only shows for read Message items (`FormType="Read"`
  rule in the manifest) — it will never appear on Compose, Calendar, or the
  inbox list with nothing open.
- Deploying a change to the add-in's HTML/JS/CSS or the manifest template is
  `npm run deploy` (i.e. `wrangler deploy`) from `CaravanCMS.Worker/`. Only
  changed static assets get re-uploaded; unrelated files are left alone.

## Deployment — sideloading is NOT available to end users anymore

Microsoft has deprecated manual sideloading ("Add from URL" / "Add from File")
for regular (non-admin) users in modern tenants. **Do not suggest sideloading
as the fix** — it was already ruled out. The only viable path is
**Microsoft 365 admin center → Integrated apps**, uploading/pointing at
`https://api.caravanland.co.nz/addin/manifest.xml` and deploying centrally to
the relevant users.

## Verifying the manifest is actually valid (don't just eyeball a browser tab)

Browsers render add-in XML with tags stripped when there's no stylesheet — a
page that looks like garbled text (`250 ReadItem false ...`) is actually the
manifest rendering *correctly*: those are `RequestedHeight`, `Permissions`,
and `DisableEntityHighlighting` values with markup stripped, not an error.
Don't mistake that for a broken manifest. `curl https://api.caravanland.co.nz/addin/manifest.xml`
is a clean, reliable check — no cert warnings to worry about since it's a
normal Cloudflare-issued TLS cert.

## Troubleshooting checklist, in order, when "the add-in is enabled but doesn't
## show up in Outlook"

1. **Manifest reachable?** `curl https://api.caravanland.co.nz/addin/manifest.xml`
   from any machine. If this fails, it's a Worker deployment/DNS issue, not a
   client-side one — check `npx wrangler deployments list` from
   `CaravanCMS.Worker/` and the `api.caravanland.co.nz` DNS/zone status in the
   Cloudflare dashboard.
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
8. **Task pane loads but shows a stale version of the JS/CSS.** Outlook's
   WebView2 can cache the static assets aggressively. Close and fully reopen
   the task pane (or restart Outlook) after any `wrangler deploy` before
   assuming a fix didn't take.

## JSON casing gotcha for anything that writes to the API

The Worker's routes do a plain, case-sensitive `JSON.parse` of request bodies
(via Hono, e.g. `c.req.json<{ name: string }>()`) — there is no ASP.NET-style
case-insensitive model binding like the old `CaravanCMS.Api` had. The add-in's
JS naturally sends camelCase and is unaffected. `CaravanCMS.Client` and
`CaravanCMS.Admin`'s `ApiClient.cs` both set
`PropertyNamingPolicy = JsonNamingPolicy.CamelCase` on their `JsonSerializerOptions`
specifically to match this — if a new WPF API call is added without reusing
the existing `JsonOpts`, or if a new C# request DTO is serialized some other
way, it will silently send PascalCase and the Worker will read `undefined`
for every field, usually surfacing as an unexplained 500.
