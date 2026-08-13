/* global Office */

// Stable production base URL — prepopulated into Settings so most users never need
// to type it, but still editable there in case the deployment ever moves again.
const DEFAULT_BASE_URL = "https://api.caravanland.co.nz";

// ── Settings (persisted per-user via Office roaming settings, synced through Exchange) ──
const Settings = {
  get baseUrl() { return (Office.context.roamingSettings.get("cavCmsBaseUrl") || DEFAULT_BASE_URL).replace(/\/$/, ""); },
  get apiKey() { return Office.context.roamingSettings.get("cavCmsApiKey") || ""; },
  get staffName() { return Office.context.roamingSettings.get("cavCmsStaffName") || ""; },
  get autoLog() {
    const v = Office.context.roamingSettings.get("cavCmsAutoLog");
    return v === undefined ? true : !!v;
  },
  get isConfigured() { return !!this.apiKey; },
  save(baseUrl, apiKey, staffName, autoLog) {
    Office.context.roamingSettings.set("cavCmsBaseUrl", baseUrl);
    Office.context.roamingSettings.set("cavCmsApiKey", apiKey);
    Office.context.roamingSettings.set("cavCmsStaffName", staffName);
    Office.context.roamingSettings.set("cavCmsAutoLog", autoLog);
    return new Promise((resolve, reject) => {
      Office.context.roamingSettings.saveAsync(result => {
        result.status === Office.AsyncResultStatus.Succeeded ? resolve() : reject(result.error);
      });
    });
  }
};

// ── Thin API client ──
const Api = {
  async request(method, path, body) {
    const res = await fetch(`${Settings.baseUrl}${path}`, {
      method,
      headers: {
        "Content-Type": "application/json",
        "X-API-Key": Settings.apiKey
      },
      body: body ? JSON.stringify(body) : undefined
    });
    if (res.status === 404) return null;
    if (!res.ok) throw new Error(`${method} ${path} failed: ${res.status} ${await res.text()}`);
    if (res.status === 204) return undefined;
    return res.json();
  },
  lookupCustomerByEmail(email) {
    return Api.request("GET", `/api/customers/lookup?email=${encodeURIComponent(email)}`);
  },
  detectCaravans(text) {
    return Api.request("POST", "/api/caravans/detect", { text });
  },
  searchCaravans(query) {
    return Api.request("GET", `/api/caravans/search?q=${encodeURIComponent(query)}`);
  },
  createConversation(customerId, subject, externalConversationId, registrationNumber) {
    return Api.request("POST", "/api/conversations", { customerId, subject, externalConversationId, registrationNumber });
  },
  addMessage(conversationId, message) {
    return Api.request("POST", `/api/conversations/${conversationId}/messages`, message);
  },
  removeMessage(conversationId, messageId) {
    return Api.request("DELETE", `/api/conversations/${conversationId}/messages/${messageId}`);
  },
  logThread(mailbox, conversationId, customerId, subject, registrationNumber, loggedBy) {
    return Api.request("POST", "/api/conversations/log-thread", {
      mailbox, conversationId, customerId, subject, registrationNumber, loggedBy
    });
  }
};

// ── Office.js promisified helpers ──
function getItemBodyText() {
  return new Promise((resolve, reject) => {
    Office.context.mailbox.item.body.getAsync(Office.CoercionType.Text, {}, result => {
      result.status === Office.AsyncResultStatus.Succeeded ? resolve(result.value) : reject(result.error);
    });
  });
}

// Which mailbox the open item actually lives in — matters because Office.context.mailbox
// .userProfile.emailAddress always reports the signed-in user's own address, even when
// they're reading an item in a shared mailbox added as its own entry in Outlook (e.g.
// info@caravanland.co.nz). getSharedPropertiesAsync only succeeds in that shared/delegate
// scenario; for a normal personal-mailbox item it fails, and we fall back to the user's own
// address. Without this, "log entire conversation" on a shared-mailbox thread would ask
// Graph to search the wrong mailbox and come back empty.
function getMailboxAddress() {
  return new Promise(resolve => {
    const item = Office.context.mailbox.item;
    if (typeof item.getSharedPropertiesAsync !== "function") {
      resolve(Office.context.mailbox.userProfile.emailAddress);
      return;
    }
    item.getSharedPropertiesAsync(result => {
      if (result.status === Office.AsyncResultStatus.Succeeded && result.value && result.value.targetMailbox) {
        resolve(result.value.targetMailbox);
      } else {
        resolve(Office.context.mailbox.userProfile.emailAddress);
      }
    });
  });
}

// ── Strip quoted reply/forward chains ──
// The plaintext body Outlook hands us includes the entire thread history below the new
// text (Outlook's own quote block, or another client's). Without trimming this, every
// reply on a long thread would re-log the whole chain as its own message body, so the same
// earlier messages end up duplicated hundreds of times over a long-running conversation.
// We cut at the first quote-chain marker found, so each logged message is just what this
// sender actually wrote — the earlier messages are already logged separately in their own
// CommunicationLog rows from when *they* came through.
// Each nesting level of a reply chain is indented with leading tabs/spaces (Apple/iPhone Mail does
// this heavily), so every marker tolerates leading whitespace rather than anchoring straight at ^.
const REPLY_CHAIN_PATTERNS = [
  /^[ \t]*_{10,}\s*$/m,                             // Outlook's "________________________________" divider
  /^[ \t]*-{5,}\s*Original Message\s*-{5,}\s*$/im,  // "-----Original Message-----"
  /^[ \t]*-{5,}\s*Forwarded Message\s*-{5,}\s*$/im, // "-----Forwarded Message-----"
  /^[ \t]*From:\s.+\r?\n[ \t]*(?:Sent|Date):\s.+$/im, // Outlook's "From: ...\nSent/Date: ..." header block
  /^[ \t]*On .{5,100} wrote:\s*$/im                 // Gmail/Apple Mail-style "On <date>, <name> wrote:"
];

function trimQuotedReplyChain(bodyText) {
  let cutIndex = bodyText.length;
  for (const pattern of REPLY_CHAIN_PATTERNS) {
    const match = pattern.exec(bodyText);
    if (match && match.index < cutIndex) cutIndex = match.index;
  }
  return bodyText.slice(0, cutIndex).trimEnd();
}

// Staff (Sent Items / replies) mail comes From a caravanland.co.nz address, not the customer's —
// treating "From" as always-the-customer meant outbound replies either failed customer lookup
// outright or, worse, got logged as Inbound. isStaffAddress() lets processCurrentItem() tell the
// two cases apart and pick the right address (From for inbound, the customer recipient for outbound)
// to match against CaravanCMS.
const STAFF_EMAIL_DOMAIN = "caravanland.co.nz";
function isStaffAddress(address) {
  return new RegExp(`@${STAFF_EMAIL_DOMAIN}$`, "i").test((address || "").trim());
}

function getCurrentItemInfo() {
  const item = Office.context.mailbox.item;
  const fromAddress = (item.from && item.from.emailAddress) || (item.sender && item.sender.emailAddress) || "";
  const toAddresses = (item.to || []).map(r => r.emailAddress).filter(Boolean);
  const direction = isStaffAddress(fromAddress) ? "Outbound" : "Inbound";
  // For an outbound (staff) message, the customer is a recipient, not the sender — match on the
  // first To address that isn't also a staff address (handles internal CC'd colleagues). If every
  // recipient is a staff address, this is a purely internal email — there's no external party to
  // match against at all, so matchAddress is left blank rather than falling back to a colleague's
  // address (that would risk matching a staff member who also happens to be a customer, wrongly
  // logging an internal chat as a customer conversation).
  const externalRecipient = toAddresses.find(a => !isStaffAddress(a));
  const isInternalOnly = direction === "Outbound" && !externalRecipient && toAddresses.length > 0;
  const matchAddress = direction === "Outbound" ? (externalRecipient || "") : fromAddress;
  return {
    itemId: item.itemId,
    subject: item.subject || "(No subject)",
    fromAddress,
    toAddresses,
    direction,
    matchAddress,
    isInternalOnly,
    conversationId: item.conversationId,
    internetMessageId: item.internetMessageId,
    dateTimeCreated: item.dateTimeCreated
  };
}

// ── Main flow ──
let currentItemToken = 0; // guards against a slower request resolving after the user has already moved to another email

async function processCurrentItem() {
  const token = ++currentItemToken;
  const statusText = document.getElementById("statusText");
  const statusDetail = document.getElementById("statusDetail");
  const undoRow = document.getElementById("undoRow");
  const confirmRow = document.getElementById("confirmRow");
  undoRow.classList.add("hidden");
  confirmRow.classList.add("hidden");
  document.getElementById("threadRow").classList.add("hidden");
  resetManualAttach();

  if (!Settings.isConfigured) {
    statusText.textContent = "Not set up yet";
    statusDetail.textContent = "Click the ⚙ icon above to enter your CaravanCMS server URL and API key.";
    return;
  }

  const info = getCurrentItemInfo();

  // Purely internal (caravanland-to-caravanland) mail is never worth capturing — skip the lookup
  // entirely rather than risk matching a staff member who also happens to be a customer. It's
  // still reachable via the manual search below if someone genuinely wants to attach it.
  if (info.isInternalOnly) {
    statusText.textContent = "Ignored — internal email";
    statusDetail.innerHTML = `<span class="muted-text">Not addressed to a customer. Use the search below to attach it manually if needed.</span>`;
    return;
  }

  statusText.textContent = "Checking sender…";
  statusDetail.textContent = "";

  let customer;
  try {
    customer = await Api.lookupCustomerByEmail(info.matchAddress);
  } catch (err) {
    if (token !== currentItemToken) return;
    statusText.textContent = "Couldn't reach CaravanCMS";
    statusDetail.textContent = err.message;
    return;
  }
  if (token !== currentItemToken) return;

  if (!customer) {
    if (info.direction === "Outbound") {
      statusText.textContent = "Ignored — not a known customer";
      statusDetail.innerHTML = `<span class="muted-text">${info.matchAddress || "(no address)"} doesn't match a CaravanCMS customer. Use the search below to attach it manually.</span>`;
    } else {
      statusText.textContent = "Unknown sender";
      statusDetail.innerHTML = `<span class="muted-text">${info.matchAddress || "(no address)"} doesn't match a CaravanCMS customer. Use the search below to attach it manually.</span>`;
    }
    return;
  }

  statusText.innerHTML = `${directionBadge(info.direction)} Matched: <span class="success-text">${escapeHtml(customer.name)}</span>`;
  statusDetail.textContent = `${customer.caravanCount} caravan(s) · ${customer.jobCount} job(s) on file`;

  const threadRow = document.getElementById("threadRow");
  const logThreadButton = document.getElementById("logThreadButton");
  threadRow.classList.remove("hidden");
  logThreadButton.disabled = false;
  logThreadButton.textContent = "Log entire conversation";
  logThreadButton.onclick = () => logThread(customer, info, token);

  if (Settings.autoLog) {
    await logEmail(customer, info, token);
  } else {
    confirmRow.classList.remove("hidden");
    document.getElementById("confirmLogButton").onclick = () => logEmail(customer, info, token);
  }
}

// Pulls every message in this conversation from the mailbox via the Worker's Graph-backed
// endpoint, instead of relying on opening each one individually — this is the "nobody has
// time to hunt down every related email" shortcut. Safe to click more than once: the backend
// dedupes by ExternalMessageId, so re-running after new replies arrive only adds what's new.
async function logThread(customer, info, token) {
  const statusDetail = document.getElementById("statusDetail");
  const logThreadButton = document.getElementById("logThreadButton");
  logThreadButton.disabled = true;
  logThreadButton.textContent = "Logging conversation…";

  try {
    const mailbox = await getMailboxAddress();
    const result = await Api.logThread(mailbox, info.conversationId, customer.id, info.subject, null, Settings.staffName || null);
    if (token !== currentItemToken) return;

    logThreadButton.textContent = "Log entire conversation";
    logThreadButton.disabled = false;
    statusDetail.innerHTML = `<span class="success-text">Conversation logged ✓</span> — ${result.messagesAdded} message(s) added${result.messagesSkipped ? `, ${result.messagesSkipped} already logged` : ""}.`;
  } catch (err) {
    if (token !== currentItemToken) return;
    logThreadButton.textContent = "Log entire conversation";
    logThreadButton.disabled = false;
    statusDetail.innerHTML = `<span class="muted-text">Couldn't log conversation: ${escapeHtml(err.message)}</span>`;
  }
}

async function logEmail(customer, info, token) {
  const statusText = document.getElementById("statusText");
  const statusDetail = document.getElementById("statusDetail");
  const undoRow = document.getElementById("undoRow");
  document.getElementById("confirmRow").classList.add("hidden");

  let bodyText = "";
  try {
    bodyText = trimQuotedReplyChain(await getItemBodyText());
  } catch { /* proceed without body if Outlook can't provide it */ }
  if (token !== currentItemToken) return;

  let registrationNumber = null;
  try {
    const detected = await Api.detectCaravans(`${info.subject}\n${bodyText}`.slice(0, 20000));
    if (detected && detected.length > 0) registrationNumber = detected[0].registrationNumber;
  } catch { /* rego detection is best-effort, not required to log the email */ }
  if (token !== currentItemToken) return;

  try {
    const conversation = await Api.createConversation(customer.id, info.subject, info.conversationId, registrationNumber);
    const message = await Api.addMessage(conversation.id, {
      type: "Email",
      direction: info.direction,
      fromAddress: info.fromAddress,
      body: bodyText.slice(0, 20000),
      externalMessageId: info.internetMessageId,
      loggedBy: Settings.staffName || null,
      occurredAt: info.dateTimeCreated ? new Date(info.dateTimeCreated).toISOString() : null
    });
    if (token !== currentItemToken) return;

    statusText.innerHTML = `${directionBadge(info.direction)} Logged to CaravanCMS ✓ — <span class="success-text">${escapeHtml(customer.name)}</span>`;
    statusDetail.textContent = registrationNumber
      ? `Linked to caravan ${registrationNumber}${conversation.subject ? " — " + conversation.subject : ""}`
      : (conversation.subject || "");

    undoRow.classList.remove("hidden");
    document.getElementById("undoButton").onclick = async () => {
      try {
        await Api.removeMessage(conversation.id, message.id);
        statusText.textContent = "Undone";
        statusDetail.textContent = "This email was removed from CaravanCMS.";
        undoRow.classList.add("hidden");
      } catch (err) {
        statusDetail.textContent = `Undo failed: ${err.message}`;
      }
    };
  } catch (err) {
    if (token !== currentItemToken) return;
    statusText.textContent = "Logging failed";
    statusDetail.textContent = err.message;
  }
}

// ── Manual attach (search) ──
let searchDebounce;
let resetManualAttach = () => {};
function initSearch() {
  const box = document.getElementById("searchBox");
  const results = document.getElementById("searchResults");
  const selected = document.getElementById("selectedCaravan");
  const attachButton = document.getElementById("attachButton");
  let chosenCaravan = null;

  // Every piece of manual-attach state (search text, results, the chosen caravan and its
  // "Attached to X" label) belongs to the email that was open when it was set. None of it
  // is valid for a different email, so ItemChanged must wipe all of it — otherwise the last
  // caravan you attached stays displayed as if it applied to whatever you open next, which
  // is misleading even though no request is actually sent until Attach is clicked again.
  resetManualAttach = () => {
    clearTimeout(searchDebounce);
    chosenCaravan = null;
    box.value = "";
    results.innerHTML = "";
    selected.classList.add("hidden");
    selected.innerHTML = "";
    attachButton.classList.add("hidden");
  };

  box.addEventListener("input", () => {
    clearTimeout(searchDebounce);
    const q = box.value.trim();
    results.innerHTML = "";
    if (q.length < 2) return;
    searchDebounce = setTimeout(async () => {
      if (!Settings.isConfigured) return;
      let matches;
      try {
        matches = await Api.searchCaravans(q);
      } catch (err) {
        results.innerHTML = `<div class="muted-text">${escapeHtml(err.message)}</div>`;
        return;
      }
      results.innerHTML = "";
      (matches || []).slice(0, 20).forEach(c => {
        const item = document.createElement("div");
        item.className = "search-result-item";
        item.innerHTML = `<span class="rego">${escapeHtml(c.registrationNumber)}</span> — ${escapeHtml(c.make || "")} ${escapeHtml(c.model || "")}<br/><span class="customer">${escapeHtml(c.customerName || "")}</span>`;
        item.onclick = () => {
          chosenCaravan = c;
          selected.classList.remove("hidden");
          selected.innerHTML = `Attach to <strong>${escapeHtml(c.registrationNumber)}</strong> — ${escapeHtml(c.make || "")} ${escapeHtml(c.model || "")} (${escapeHtml(c.customerName || "")})`;
          attachButton.classList.remove("hidden");
          results.innerHTML = "";
          box.value = "";
        };
        results.appendChild(item);
      });
    }, 300);
  });

  attachButton.addEventListener("click", async () => {
    if (!chosenCaravan) return;
    attachButton.disabled = true;
    try {
      const info = getCurrentItemInfo();
      let bodyText = "";
      try { bodyText = trimQuotedReplyChain(await getItemBodyText()); } catch { /* best effort */ }

      const conversation = await Api.createConversation(
        chosenCaravan.customerId, info.subject, info.conversationId, chosenCaravan.registrationNumber);
      await Api.addMessage(conversation.id, {
        type: "Email",
        direction: info.direction,
        fromAddress: info.fromAddress,
        body: bodyText.slice(0, 20000),
        externalMessageId: info.internetMessageId,
        loggedBy: Settings.staffName || null,
        occurredAt: info.dateTimeCreated ? new Date(info.dateTimeCreated).toISOString() : null
      });

      selected.innerHTML = `<span class="success-text">Attached to ${escapeHtml(chosenCaravan.registrationNumber)} ✓</span>`;
      attachButton.classList.add("hidden");
      chosenCaravan = null;
    } catch (err) {
      selected.innerHTML = `<span class="muted-text">Failed: ${escapeHtml(err.message)}</span>`;
    } finally {
      attachButton.disabled = false;
    }
  });
}

// ── Settings panel ──
function initSettingsPanel() {
  const panel = document.getElementById("settingsPanel");
  const main = document.getElementById("mainPanel");

  document.getElementById("settingsToggle").addEventListener("click", () => {
    document.getElementById("baseUrl").value = Settings.baseUrl;
    document.getElementById("apiKey").value = Settings.apiKey;
    document.getElementById("staffName").value = Settings.staffName;
    document.getElementById("autoLog").checked = Settings.autoLog;
    panel.classList.remove("hidden");
    main.classList.add("hidden");
  });

  document.getElementById("cancelSettings").addEventListener("click", () => {
    panel.classList.add("hidden");
    main.classList.remove("hidden");
  });

  document.getElementById("saveSettings").addEventListener("click", async () => {
    const baseUrl = document.getElementById("baseUrl").value.trim();
    const apiKey = document.getElementById("apiKey").value.trim();
    const staffName = document.getElementById("staffName").value.trim();
    const autoLog = document.getElementById("autoLog").checked;
    await Settings.save(baseUrl, apiKey, staffName, autoLog);
    panel.classList.add("hidden");
    main.classList.remove("hidden");
    processCurrentItem();
  });

  if (!Settings.isConfigured) {
    document.getElementById("settingsToggle").click();
  }
}

function directionBadge(direction) {
  const cls = direction === "Outbound" ? "badge-outbound" : "badge-inbound";
  return `<span class="direction-badge ${cls}">${direction}</span>`;
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));
}

Office.onReady(info => {
  if (info.host !== Office.HostType.Outlook) return;

  initSettingsPanel();
  initSearch();

  // Auto-process the email currently open, and again every time the user reads a different
  // one while this pane stays pinned open — this is what makes detection feel "automatic".
  processCurrentItem();
  Office.context.mailbox.addHandlerAsync(Office.EventType.ItemChanged, processCurrentItem);
});
