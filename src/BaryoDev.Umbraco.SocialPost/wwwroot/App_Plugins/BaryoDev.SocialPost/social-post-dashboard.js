// Plain custom element with no build step, same approach as the other BaryoDev dashboards.
const API = "/umbraco/management/api/v1/social-post";
const BASE = "/App_Plugins/BaryoDev.SocialPost";

const Base = await import("@umbraco-cms/backoffice/element-api")
  .then(({ UmbElementMixin }) => UmbElementMixin(HTMLElement))
  .catch(() => HTMLElement);

const STATUS_CLASS = { pending: "warn", sending: "warn", sent: "", failed: "bad", cancelled: "mute" };

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// Only http and https links are rendered as links.
function safeHref(value) {
  try {
    const u = new URL(value);
    return u.protocol === "https:" || u.protocol === "http:" ? u.href : null;
  } catch { return null; }
}

function pickerReady() {
  if (customElements.get("umb-input-document")) return Promise.resolve(true);
  let timer;
  const timeout = new Promise((resolve) => { timer = setTimeout(() => resolve(false), 3000); });
  const defined = customElements.whenDefined("umb-input-document").then(() => true, () => false);
  return Promise.race([defined, timeout]).finally(() => clearTimeout(timer));
}

const tokens = new WeakMap();

async function token(host) {
  if (!tokens.has(host)) {
    tokens.set(host, Promise.all([
      import("@umbraco-cms/backoffice/context-api"),
      import("@umbraco-cms/backoffice/auth"),
    ]).then(([{ UmbContextConsumer }, { UMB_AUTH_CONTEXT }]) => {
      const consumer = new UmbContextConsumer(host, UMB_AUTH_CONTEXT);
      const auth = consumer.asPromise();
      consumer.request();
      return auth;
    }).catch(() => null));
  }
  const auth = await tokens.get(host);
  return auth ? auth.getLatestToken() : null;
}

class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function call(host, path, method = "GET", body = undefined) {
  const t = await token(host);
  const response = await fetch(API + path, {
    method,
    credentials: "same-origin",
    headers: {
      ...(t ? { Authorization: `Bearer ${t}` } : {}),
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401 || response.status === 403) throw new ApiError(response.status, "You do not have access to this.");
  if (!response.ok) {
    let detail = "";
    try {
      const j = await response.json();
      detail = j.error ?? j.detail ?? "";
    } catch { /* no body */ }
    throw new ApiError(response.status, detail || `Request failed (${response.status}).`);
  }
  if (response.status === 204) return null;
  return (response.headers.get("content-type") ?? "").includes("json") ? response.json() : null;
}

class SocialPostDashboard extends Base {
  #networks = [];
  #queue = [];
  #error = "";
  #notice = "";
  #timer = null;
  #open = new Set();
  #hasPicker = false;
  #disconnected = false;

  async connectedCallback() {
    super.connectedCallback?.();
    this.#disconnected = false;
    this.#hasPicker = await pickerReady();
    if (this.#disconnected) return;
    this.innerHTML = this.#shell();
    this.querySelector("#sp-post").addEventListener("click", () => this.#postNow());
    this.querySelector("#sp-share").addEventListener("click", () => this.#share());
    this.querySelector("#sp-refresh").addEventListener("click", () => this.#load());
    this.querySelector("#sp-nets").addEventListener("click", (e) => this.#onNetworkClick(e));
    this.querySelector("#sp-queue").addEventListener("click", (e) => this.#onQueueClick(e));
    await this.#load();
    // Skip a refresh while a credential form is being edited.
    this.#timer = setInterval(() => { if (!this.querySelector("#sp-nets :focus")) this.#loadQueue(); }, 10000);
  }

  disconnectedCallback() {
    super.disconnectedCallback?.();
    this.#disconnected = true;
    clearInterval(this.#timer);
  }

  #shell() {
    return `
      <link rel="stylesheet" href="${BASE}/brand.css">
      <div class="sp">
        <div class="sp-head"><img src="${BASE}/logo.svg" alt="baryo.dev"><span>Social post</span></div>
        <div id="sp-banner"></div>
        <div class="sp-box"><h3>Networks</h3><div id="sp-nets" class="sp-cards"></div></div>
        <div class="sp-box">
          <h3>Post now</h3>
          <div class="sp-row"><label for="sp-content">Content</label>
            ${this.#hasPicker ? `<umb-input-document id="sp-content" max="1"></umb-input-document>` : `<input id="sp-content" placeholder="Content key (GUID)">`}</div>
          <div class="sp-row"><label>Networks</label><div id="sp-pick" class="sp-nets"></div>
            <div class="sp-note">Leave all unticked to use every enabled network.</div></div>
          <label class="sp-check"><input type="checkbox" id="sp-force"> Post again even if it was posted before</label>
          <div class="sp-actions"><button class="sp-btn primary" id="sp-post" type="button">Post now</button>
            <button class="sp-btn" id="sp-share" type="button">Share links</button></div>
          <div id="sp-result"></div>
          <div id="sp-links"></div>
        </div>
        <div class="sp-box">
          <div class="sp-actions"><h3>Queue</h3><button class="sp-btn small" id="sp-refresh" type="button">Refresh</button></div>
          <div id="sp-queue"></div>
        </div>
      </div>`;
  }

  async #load() {
    try {
      this.#networks = await call(this, "/networks");
      this.#error = "";
    } catch (e) {
      this.#error = e.message;
    }
    this.#renderNetworks();
    await this.#loadQueue();
  }

  async #loadQueue() {
    try {
      this.#queue = await call(this, "/queue");
    } catch (e) {
      this.#error = e.message;
    }
    this.#renderQueue();
    this.#renderBanner();
  }

  #renderBanner() {
    this.querySelector("#sp-banner").innerHTML =
      (this.#error ? `<div class="sp-err" role="alert">${esc(this.#error)}</div>` : "")
      + (this.#notice ? `<div class="sp-msg">${esc(this.#notice)}</div>` : "");
  }

  #renderNetworks() {
    this.querySelector("#sp-nets").innerHTML = this.#networks.map((n) => {
      const state = !n.enabled ? `<span class="sp-tag mute">Off</span>`
        : n.configured ? `<span class="sp-tag">On</span>` : `<span class="sp-tag warn">Not configured</span>`;
      const open = this.#open.has(n.name);
      const form = open ? `
        <div class="sp-row">${n.enabledSource === "config"
          ? `<div class="sp-note">Enabled in configuration.</div>`
          : `<label class="sp-check"><input type="checkbox" data-enabled ${n.enabled ? "checked" : ""}> Enabled</label>`}</div>
        ${n.fields.map((f) => this.#field(f)).join("")}
        <div class="sp-actions"><button class="sp-btn small primary" data-save type="button">Save</button></div>` : "";
      return `<div class="sp-card" data-net="${esc(n.name)}">
        <div class="sp-card-head"><span>${esc(n.name)}</span>${state}</div>
        <div class="sp-note">Limit ${n.maxLength} characters</div>
        <div class="sp-actions"><button class="sp-btn small" data-toggle type="button">${open ? "Close" : "Settings"}</button></div>
        ${form}</div>`;
    }).join("");
    this.querySelector("#sp-pick").innerHTML = this.#networks.map((n) =>
      `<label class="sp-check"><input type="checkbox" data-pick="${esc(n.name)}"> ${esc(n.name)}</label>`).join("");
  }

  #field(f) {
    const fromConfig = f.source === "config" || f.source === "locked";
    const note = f.source === "locked" ? "set in configuration only, because a secret comes from configuration" : "from configuration";
    const id = `sp-f-${f.key}`;
    if (f.secret) {
      // A stored secret is never sent back. The field only says whether one is set.
      return `<div class="sp-row"><label for="${id}">${esc(f.label)} <span class="sp-tag ${f.isSet ? "" : "warn"}">${f.isSet ? "set" : "not set"}</span>${fromConfig ? ` <span class="sp-note">${note}</span>` : ""}</label>
        <input id="${id}" data-key="${esc(f.key)}" type="password" autocomplete="new-password" ${fromConfig ? "disabled" : ""} placeholder="${f.isSet ? "Leave blank to keep it" : ""}">
        ${f.isSet && !fromConfig ? `<label class="sp-check"><input type="checkbox" data-clear="${esc(f.key)}"> Remove the stored value</label>` : ""}</div>`;
    }
    return `<div class="sp-row"><label for="${id}">${esc(f.label)}${fromConfig ? ` <span class="sp-note">${note}</span>` : ""}</label>
      <input id="${id}" data-key="${esc(f.key)}" value="${esc(f.value ?? "")}" ${fromConfig ? "disabled" : ""}></div>`;
  }

  async #onNetworkClick(e) {
    const card = e.target.closest("[data-net]");
    if (!card) return;
    const name = card.dataset.net;
    if (e.target.closest("[data-toggle]")) {
      if (this.#open.has(name)) this.#open.delete(name); else this.#open.add(name);
      this.#renderNetworks();
      return;
    }
    if (!e.target.closest("[data-save]")) return;
    const values = {};
    for (const input of card.querySelectorAll("input[data-key]:not(:disabled)")) values[input.dataset.key] = input.value;
    const clear = [...card.querySelectorAll("input[data-clear]:checked")].map((c) => c.dataset.clear);
    const enabledBox = card.querySelector("input[data-enabled]");
    try {
      await call(this, `/networks/${encodeURIComponent(name)}`, "PUT", { enabled: enabledBox ? enabledBox.checked : null, values, clear });
      this.#notice = `${name} saved.`;
      this.#error = "";
      this.#open.delete(name);
      await this.#load();
    } catch (err) {
      this.#error = err.message;
    }
    this.#renderBanner();
  }

  #renderQueue() {
    const host = this.querySelector("#sp-queue");
    if (this.#queue.length === 0) {
      host.innerHTML = `<div class="sp-note">Nothing queued yet.</div>`;
      return;
    }
    const rows = this.#queue.map((r) => {
      const link = safeHref(r.remoteUrl);
      const next = r.status === "pending" ? new Date(r.nextAttemptUtc).toLocaleString() : "";
      return `<tr data-id="${r.id}">
        <td>${esc(r.title)}</td><td>${esc(r.network)}</td>
        <td><span class="sp-tag ${STATUS_CLASS[r.status] ?? ""}">${esc(r.status)}</span>${r.lastError ? `<div class="sp-note">${esc(r.lastError)}</div>` : ""}</td>
        <td>${r.attempts}</td><td>${esc(next)}</td>
        <td>${link ? `<a href="${esc(link)}" target="_blank" rel="noopener noreferrer">Open</a>` : ""}</td>
        <td class="sp-actions">${r.status === "failed" || r.status === "cancelled" ? `<button class="sp-btn small" data-retry type="button">Retry</button>` : ""}
          ${r.status === "pending" ? `<button class="sp-btn small" data-cancel type="button">Cancel</button>` : ""}</td></tr>`;
    }).join("");
    host.innerHTML = `<table class="sp-table"><thead><tr><th>Title</th><th>Network</th><th>Status</th><th>Tries</th><th>Next try</th><th>Post</th><th></th></tr></thead><tbody>${rows}</tbody></table>`;
  }

  async #onQueueClick(e) {
    const row = e.target.closest("tr[data-id]");
    const action = e.target.closest("[data-retry]") ? "retry" : e.target.closest("[data-cancel]") ? "cancel" : null;
    if (!row || !action) return;
    try {
      await call(this, `/queue/${row.dataset.id}/${action}`, "POST");
      this.#error = "";
    } catch (err) {
      this.#error = err.message;
    }
    await this.#loadQueue();
  }

  #pickedKey() {
    const picker = this.querySelector("#sp-content");
    return this.#hasPicker ? (picker.selection?.[0] ?? "") : picker.value.trim();
  }

  // Share pages for X, Facebook and LinkedIn. The links come from the server, which builds them from the content.
  async #share() {
    const raw = this.#pickedKey();
    const host = this.querySelector("#sp-links");
    if (!raw) {
      this.#error = "Pick the content to share.";
      this.#renderBanner();
      return;
    }
    try {
      const info = await call(this, `/share/${encodeURIComponent(raw)}`);
      this.#error = "";
      const names = { x: "X", facebook: "Facebook", linkedin: "LinkedIn" };
      host.innerHTML = `<div class="sp-actions">${Object.entries(info.links).map(([key, href]) => {
        const safe = safeHref(href);
        return safe ? `<a class="sp-btn small" href="${esc(safe)}" target="_blank" rel="noopener noreferrer">${esc(names[key] ?? key)}</a>` : "";
      }).join("")}</div>`;
      // Added by script, so it never shows without JavaScript or without clipboard access.
      if (navigator.clipboard?.writeText) {
        const copy = document.createElement("button");
        copy.type = "button";
        copy.className = "sp-btn small";
        copy.textContent = "Copy link";
        copy.addEventListener("click", async () => {
          try {
            await navigator.clipboard.writeText(`${info.text}\n${info.url}`);
            copy.textContent = "Copied";
          } catch {
            copy.textContent = "Copy failed";
          }
          setTimeout(() => { copy.textContent = "Copy link"; }, 2000);
        });
        host.querySelector(".sp-actions").append(copy);
      }
    } catch (err) {
      host.innerHTML = "";
      this.#error = err.message;
    }
    this.#renderBanner();
  }

  async #postNow() {
    const raw = this.#pickedKey();
    if (!raw) {
      this.#error = "Pick the content to post.";
      this.#renderBanner();
      return;
    }
    const body = {
      contentKey: raw,
      networks: [...this.querySelectorAll("input[data-pick]:checked")].map((c) => c.dataset.pick),
      force: this.querySelector("#sp-force").checked,
    };
    try {
      const results = await call(this, "/post-now", "POST", body);
      this.#error = "";
      this.querySelector("#sp-result").innerHTML = results.map((r) =>
        `<div class="sp-note">${esc(r.network || "Content")}: ${esc(r.outcome)}${r.reason ? `, ${esc(r.reason)}` : ""}</div>`).join("");
    } catch (err) {
      this.#error = err.message;
    }
    this.#renderBanner();
    await this.#loadQueue();
  }
}

customElements.define("baryodev-social-post-dashboard", SocialPostDashboard);

export default SocialPostDashboard;
export { SocialPostDashboard as element };
