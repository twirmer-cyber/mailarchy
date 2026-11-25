// MailArchy Web – Oberfläche. Mail-Daten werden ausschließlich über textContent/DOM-APIs eingefügt (nie innerHTML).
"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const PAGE_SIZE = 50;          // Ordneransicht
  const SEARCH_LIMIT = 500;      // Suche: bis zu 500 Treffer auf einmal, danach „500+“
  const DEBOUNCE_MS = 350;
  const MIN_CHARS = 2;
  const SVG_NS = "http://www.w3.org/2000/svg";
  const { t, lang } = window.I18N;
  window.I18N.apply(document);

  /** Intl-Formatierer für die Browser-Locale (z. B. en-GB); bei ungültigem Tag die reine Sprache. */
  function intl(Ctor, options) {
    try { return new Ctor(window.I18N.locale, options); } catch { return new Ctor(lang, options); }
  }
  const nf = intl(Intl.NumberFormat);
  const nf1 = intl(Intl.NumberFormat, { minimumFractionDigits: 1, maximumFractionDigits: 1 });

  const state = {
    user: null,
    folderId: null,
    folderName: "",
    folderHasChildren: false,
    mode: "none",          // "none" | "folder" | "search"
    offset: 0,
    total: 0,
    more: false,
    activeMessage: null,
    filters: { range: "all", att: false, sort: "date", scope: false },
    lastQuery: "",
    listCtrl: null,        // AbortController der laufenden Listenanfrage
    msgCtrl: null,
    helpLoaded: false,
  };

  // ---------------------------------------------------------------- Hilfsfunktionen

  function el(tag, props, ...children) {
    const e = document.createElement(tag);
    if (props) {
      for (const [k, v] of Object.entries(props)) {
        if (v === undefined || v === null || v === false) continue;
        if (k === "class") e.className = v;
        else if (k === "text") e.textContent = v;
        else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
        else e.setAttribute(k, v === true ? "" : v);
      }
    }
    for (const c of children) if (c != null) e.append(c);
    return e;
  }

  const ICONS = {
    clip: { box: "0 0 16 16", d: ["M13.2 7.6 8 12.8a3.3 3.3 0 0 1-4.7-4.7l5.6-5.6a2.2 2.2 0 0 1 3.1 3.1L6.4 11.2a1.1 1.1 0 0 1-1.6-1.6L9.9 4.5"] },
    chevron: { box: "0 0 16 16", d: ["M6 3.5 10.5 8 6 12.5"] },
  };

  function icon(name, cls, label) {
    const def = ICONS[name];
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", def.box);
    svg.setAttribute("class", "icon" + (cls ? " " + cls : ""));
    if (label) { svg.setAttribute("role", "img"); svg.setAttribute("aria-label", label); }
    else svg.setAttribute("aria-hidden", "true");
    for (const d of def.d) {
      const p = document.createElementNS(SVG_NS, "path");
      p.setAttribute("d", d);
      svg.append(p);
    }
    return svg;
  }

  const dateShort = intl(Intl.DateTimeFormat, { day: "2-digit", month: "2-digit", year: "numeric" });
  const timeShort = intl(Intl.DateTimeFormat, { hour: "2-digit", minute: "2-digit" });
  const dateLong = intl(Intl.DateTimeFormat, { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });

  function formatListDate(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return "";
    return d.toDateString() === new Date().toDateString() ? timeShort.format(d) : dateShort.format(d);
  }

  function formatSize(n) {
    if (!n) return "";
    if (n < 1024) return n + " B";
    if (n < 1024 * 1024) return Math.round(n / 1024) + " KB";
    return nf1.format(n / 1024 / 1024) + " MB";
  }

  /** Anzeigename ohne &lt;Adresse&gt; (Server liefert fromName; Fallback für ältere Antworten). */
  function senderName(m) {
    if (m.fromName) return m.fromName;
    const raw = (m.from || "").split(",")[0].trim();
    const match = /^"?([^"<]*?)"?\s*<([^>]+)>/.exec(raw);
    return match ? (match[1].trim() || match[2]) : raw;
  }

  class AuthError extends Error {}

  async function api(path, options = {}) {
    const opts = { credentials: "same-origin", headers: {}, ...options };
    opts.headers["Accept-Language"] = lang; // Fehlermeldungen und Suchhilfe des Servers in der Sprache der Oberfläche
    if (opts.method && opts.method !== "GET") {
      opts.headers["X-MailVault"] = "1";
      if (opts.body !== undefined) {
        opts.headers["Content-Type"] = "application/json";
        opts.body = JSON.stringify(opts.body);
      }
    }
    let res;
    try {
      res = await fetch(path, opts);
    } catch (e) {
      if (e && e.name === "AbortError") throw e;
      const err = new Error(t("error.network"));
      err.network = true;
      throw err;
    }
    if (res.status === 401 && path !== "/api/login") {
      showLogin();
      throw new AuthError(t("error.notSignedIn"));
    }
    let data = null;
    if (res.status !== 204) {
      try { data = await res.json(); } catch { data = null; }
    }
    if (!res.ok) {
      const err = new Error((data && data.error) || t("error.http", { status: res.status }));
      err.status = res.status;
      throw err;
    }
    return data;
  }

  const isAbort = (e) => e && e.name === "AbortError";
  function setView(view) { $("panes").dataset.view = view; }

  function emptyState(title, text, actions, isError) {
    const box = el("div", { class: "list-empty" + (isError ? " state-error" : ""), role: isError ? "alert" : null },
      el("p", { class: "empty-title", text: title }),
      text ? el("p", { class: "empty-text", text }) : null);
    if (actions && actions.length) box.append(el("div", { class: "empty-actions" }, ...actions));
    return box;
  }

  function skeletonRows(n) {
    const rows = [];
    for (let i = 0; i < n; i++) rows.push(el("div", { class: "skel", "aria-hidden": "true" }, el("span"), el("span")));
    return rows;
  }

  // ---------------------------------------------------------------- Anmeldung

  function showLogin() {
    state.user = null;
    abortList();
    $("app").hidden = true;
    $("login").hidden = false;
    $("login-pass").value = "";
    $("r-body").removeAttribute("src");
    $("login-user").focus();
  }

  async function showApp(me) {
    state.user = me;
    $("user-name").textContent = me.user + (me.isAdmin ? " · " + t("user.admin") : "");
    $("login").hidden = true;
    $("app").hidden = false;
    await loadFolders();
  }

  $("login-form").addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const errorBox = $("login-error");
    const user = $("login-user").value.trim();
    const pass = $("login-pass").value;
    errorBox.hidden = true;
    if (!user || !pass) {
      errorBox.textContent = !user ? t("login.needUser") : t("login.needPass");
      errorBox.hidden = false;
      (!user ? $("login-user") : $("login-pass")).focus();
      return;
    }
    const btn = $("login-btn");
    btn.disabled = true;
    btn.textContent = t("login.busy");
    try {
      const me = await api("/api/login", { method: "POST", body: { user, password: pass } });
      $("login-pass").value = "";
      await showApp(me);
    } catch (e) {
      errorBox.textContent = e.status === 401
        ? t("login.wrong")
        : e.message;
      errorBox.hidden = false;
      $("login-pass").select();
    } finally {
      btn.disabled = false;
      btn.textContent = t("login.submit");
    }
  });

  $("logout-btn").addEventListener("click", async () => {
    try { await api("/api/logout", { method: "POST" }); } catch { /* egal */ }
    clearReader();
    $("message-list").replaceChildren();
    $("folder-tree").replaceChildren();
    $("search-q").value = "";
    state.mode = "none";
    showLogin();
  });

  // ---------------------------------------------------------------- Ordner

  async function loadFolders() {
    const tree = $("folder-tree");
    tree.setAttribute("aria-busy", "true");
    tree.replaceChildren(...skeletonRows(6));
    $("message-list").replaceChildren(...skeletonRows(8));
    let folders;
    try { folders = await api("/api/folders"); } catch (e) {
      if (e instanceof AuthError) return;
      tree.replaceChildren(emptyState(t("folders.loadFailed"), e.message,
        [el("button", { type: "button", class: "btn", text: t("action.retry"), onclick: loadFolders })], true));
      $("message-list").replaceChildren();
      tree.removeAttribute("aria-busy");
      return;
    }
    tree.removeAttribute("aria-busy");
    if (folders.length === 0) {
      tree.replaceChildren(emptyState(t("folders.noneTitle"), t("folders.noneText")));
      renderNoSelection();
      return;
    }
    const ul = el("ul");
    for (const f of folders) ul.append(folderNode(f, true, 1));
    tree.replaceChildren(ul);

    // Ersten sinnvollen Ordner öffnen (Posteingang bevorzugt), damit niemand vor einer leeren Fläche steht.
    const start = findStartFolder(folders);
    const btn = start && tree.querySelector(`.folder-btn[data-id="${CSS.escape(String(start.id))}"]`);
    if (btn) { revealNode(btn); selectFolder(start, btn, true); }
    else renderNoSelection();
  }

  function findStartFolder(folders) {
    const all = [];
    (function walk(list) { for (const f of list) { all.push(f); if (f.children) walk(f.children); } })(folders);
    return all.find((f) => /^(inbox|posteingang|boîte de réception|bandeja de entrada)$/i.test(f.name) && f.count > 0)
      || all.find((f) => f.count > 0) || all[0] || null;
  }

  function revealNode(btn) {
    let li = btn.closest("li");
    while (li) {
      const ul = li.parentElement;
      if (ul && ul.hidden) {
        ul.hidden = false;
        const t = ul.parentElement && ul.parentElement.querySelector(":scope > .node-row > .toggle");
        if (t) t.setAttribute("aria-expanded", "true");
      }
      li = ul ? ul.closest("li") : null;
    }
  }

  function folderNode(f, expanded, level) {
    const li = el("li");
    const hasChildren = f.children && f.children.length > 0;
    const childUl = el("ul");
    childUl.hidden = !expanded;
    const toggle = el("button", {
      type: "button", class: "toggle" + (hasChildren ? "" : " leaf"), tabindex: hasChildren ? null : "-1",
      "aria-label": t("folder.toggle", { name: f.name }),
      "aria-expanded": hasChildren ? String(expanded) : null,
    }, icon("chevron"));
    toggle.addEventListener("click", () => {
      childUl.hidden = !childUl.hidden;
      toggle.setAttribute("aria-expanded", String(!childUl.hidden));
    });
    const mails = t("count.mails", { count: nf.format(f.total || 0) });
    const btn = el("button", { type: "button", class: "folder-btn", title: f.path || f.name, "aria-label": f.name + ", " + mails },
      el("span", { class: "folder-name", text: f.name }),
      el("span", { class: "folder-count", text: nf.format(f.total || 0), "aria-label": mails }));
    btn.dataset.id = String(f.id);
    btn.addEventListener("click", () => selectFolder(f, btn, false));
    li.append(el("div", { class: "node-row" }, toggle, btn));
    if (hasChildren) {
      for (const c of f.children) childUl.append(folderNode(c, false, level + 1));
      li.append(childUl);
    }
    return li;
  }

  function selectFolder(f, btn, initial) {
    document.querySelectorAll(".folder-btn.active").forEach((b) => { b.classList.remove("active"); b.removeAttribute("aria-current"); });
    btn.classList.add("active");
    btn.setAttribute("aria-current", "true");
    state.folderId = f.id;
    state.folderName = f.name;
    state.folderHasChildren = !!(f.children && f.children.length);
    updateScopeChip();
    // Gewählter Ordner = Suchbereich (Ordner samt Unterordnern); der Chip „Nur in …“ hebt das auf.
    state.filters.scope = true;
    setPressed($("scope-chip"), true);
    setView("list");
    // Suche auf diesen Ordner beschränkt? Dann dort weitersuchen, sonst Ordnerinhalt zeigen.
    if (state.mode === "search" && state.filters.scope) { runSearch(); return; }
    if (!initial) { $("search-q").value = ""; updateClear(); hideSearchError(); }
    state.mode = "folder";
    state.offset = 0;
    loadList();
  }

  function renderNoSelection() {
    state.mode = "none";
    $("list-title").textContent = t("list.archive");
    $("list-count").textContent = "";
    $("recursive-wrap").hidden = true;
    $("pager").hidden = true;
    $("message-list").replaceChildren(emptyState(t("list.noSelectionTitle"), t("list.noSelectionText")));
  }

  $("recursive").addEventListener("change", () => {
    if (state.mode === "folder" && state.folderId != null) { state.offset = 0; loadList(); }
  });

  $("folders-back").addEventListener("click", () => setView("folders"));

  // ---------------------------------------------------------------- Suche: Eingabe

  const input = $("search-q");
  const phoneQuery = window.matchMedia("(max-width: 759px)");
  const setPlaceholder = () => { input.placeholder = phoneQuery.matches ? t("search.placeholderShort") : t("search.placeholder"); };
  setPlaceholder();
  phoneQuery.addEventListener("change", setPlaceholder);
  let debounceTimer = 0;

  function updateClear() { $("search-clear").hidden = input.value.length === 0; }

  input.addEventListener("input", () => {
    updateClear();
    clearTimeout(debounceTimer);
    const q = input.value.trim();
    if (q.length === 0) { debounceTimer = setTimeout(leaveSearch, 150); return; }
    if (q.length < MIN_CHARS) return;
    debounceTimer = setTimeout(runSearch, DEBOUNCE_MS);
  });

  input.addEventListener("keydown", (ev) => {
    if (ev.key === "Escape" && input.value) { ev.preventDefault(); clearSearch(); }
  });

  $("search-form").addEventListener("submit", (ev) => {
    ev.preventDefault();
    clearTimeout(debounceTimer);
    if (input.value.trim().length === 0 && !filtersActive()) { leaveSearch(); return; }
    runSearch();
    input.blur(); // schließt die Tastatur auf dem Telefon
  });

  $("search-clear").addEventListener("click", () => { clearSearch(); input.focus(); });

  function clearSearch() {
    input.value = "";
    updateClear();
    clearTimeout(debounceTimer);
    leaveSearch();
  }

  function filtersActive() { return state.filters.range !== "all" || state.filters.att; }

  /** Zurück zur Ordneransicht, sobald weder Suchtext noch Filter gesetzt sind. */
  function leaveSearch() {
    hideSearchError();
    if (filtersActive()) { runSearch(); return; }
    if (state.mode !== "search") return;
    abortList();
    if (state.folderId != null) { state.mode = "folder"; state.offset = 0; loadList(); }
    else renderNoSelection();
  }

  // ---------------------------------------------------------------- Suche: Filter-Chips

  function setPressed(btn, on) { btn.setAttribute("aria-pressed", String(on)); }

  document.querySelectorAll("#range-chips .chip").forEach((chip) => {
    chip.addEventListener("click", () => {
      state.filters.range = chip.dataset.range;
      document.querySelectorAll("#range-chips .chip").forEach((c) => setPressed(c, c === chip));
      filtersChanged();
    });
  });

  $("att-chip").addEventListener("click", () => {
    state.filters.att = !state.filters.att;
    setPressed($("att-chip"), state.filters.att);
    filtersChanged();
  });

  $("scope-chip").addEventListener("click", () => {
    state.filters.scope = !state.filters.scope;
    setPressed($("scope-chip"), state.filters.scope);
    if (state.mode === "search") runSearch();
  });

  document.querySelectorAll(".segmented button").forEach((b) => {
    b.addEventListener("click", () => {
      state.filters.sort = b.dataset.sort;
      document.querySelectorAll(".segmented button").forEach((x) => setPressed(x, x === b));
      if (state.mode === "search") runSearch();
    });
  });

  function updateScopeChip() {
    const chip = $("scope-chip");
    chip.hidden = state.folderId == null;
    chip.textContent = t("filter.scope", { name: state.folderName });
  }

  function filtersChanged() {
    if (input.value.trim().length >= MIN_CHARS || filtersActive() || state.mode === "search") {
      if (input.value.trim().length === 0 && !filtersActive()) leaveSearch();
      else runSearch();
    }
  }

  function resetFilters() {
    state.filters.range = "all";
    state.filters.att = false;
    document.querySelectorAll("#range-chips .chip").forEach((c) => setPressed(c, c.dataset.range === "all"));
    setPressed($("att-chip"), false);
  }

  // ---------------------------------------------------------------- Suche: Hilfe

  $("help-btn").addEventListener("click", async () => {
    const panel = $("search-help");
    panel.hidden = !panel.hidden;
    $("help-btn").setAttribute("aria-expanded", String(!panel.hidden));
    if (!panel.hidden && !state.helpLoaded) await loadHelp();
  });

  async function loadHelp() {
    const list = $("help-list");
    list.replaceChildren(el("p", { class: "empty-text", text: t("help.loading") }));
    let data;
    try { data = await api("/api/search/help"); } catch (e) {
      if (!(e instanceof AuthError)) list.replaceChildren(el("p", { class: "form-error", text: t("help.failed") + " " + e.message }));
      return;
    }
    state.helpLoaded = true;
    list.replaceChildren();
    for (const h of data.examples) {
      const ex = el("button", { type: "button", class: "example", text: h.example, title: t("help.apply") });
      ex.addEventListener("click", () => {
        const cur = input.value.trim();
        input.value = (cur ? cur + " " : "") + h.example.replace(/\s{2,}/g, " ");
        updateClear();
        input.focus();
        runSearch();
      });
      list.append(el("div", { class: "help-item" }, el("dt", null, ex), el("dd", { text: h.meaning })));
    }
  }

  function showSearchError(msg) {
    const box = $("search-error");
    box.textContent = t("search.errorHint", { message: msg });
    box.hidden = false;
    input.setAttribute("aria-invalid", "true");
  }
  function hideSearchError() {
    $("search-error").hidden = true;
    input.removeAttribute("aria-invalid");
  }

  // ---------------------------------------------------------------- Suche: Ausführen

  function runSearch() {
    clearTimeout(debounceTimer);
    const q = input.value.trim();
    if (!q && !filtersActive()) { leaveSearch(); return; }
    state.mode = "search";
    state.lastQuery = q;
    state.offset = 0;
    if ($("panes").dataset.view !== "list") setView("list");
    loadList();
  }

  function searchUrl() {
    const p = new URLSearchParams();
    if (state.lastQuery) p.set("q", state.lastQuery);
    if (state.filters.range !== "all") p.set("range", state.filters.range);
    if (state.filters.att) p.set("attachments", "1");
    p.set("sort", state.filters.sort);
    if (state.filters.scope && state.folderId != null) p.set("folder", String(state.folderId));
    p.set("limit", String(SEARCH_LIMIT));
    return "/api/search?" + p.toString();
  }

  // ---------------------------------------------------------------- Liste

  function abortList() {
    if (state.listCtrl) { state.listCtrl.abort(); state.listCtrl = null; }
  }

  async function loadList() {
    const list = $("message-list");
    const title = $("list-title");
    const count = $("list-count");
    $("recursive-wrap").hidden = state.mode !== "folder" || !state.folderHasChildren;
    let url;
    if (state.mode === "folder") {
      title.textContent = state.folderName;
      const rec = $("recursive").checked ? "&recursive=1" : "";
      url = `/api/folders/${encodeURIComponent(state.folderId)}/messages?offset=${state.offset}&limit=${PAGE_SIZE}${rec}`;
    } else if (state.mode === "search") {
      title.textContent = state.lastQuery ? t("list.searchTitle", { query: state.lastQuery }) : t("list.filtered");
      url = searchUrl();
    } else return;

    abortList();
    const ctrl = new AbortController();
    state.listCtrl = ctrl;
    count.textContent = "";
    list.setAttribute("aria-busy", "true");
    list.replaceChildren(...skeletonRows(9));
    $("pager").hidden = true;

    let data;
    try { data = await api(url, { signal: ctrl.signal }); } catch (e) {
      if (isAbort(e) || e instanceof AuthError) return;
      if (state.listCtrl !== ctrl) return;
      list.removeAttribute("aria-busy");
      if (state.mode === "search" && e.status === 400) {
        showSearchError(e.message);
        list.replaceChildren(emptyState(t("search.notUnderstood"), e.message, [
          el("button", { type: "button", class: "btn", text: t("help.show"), onclick: openHelp }),
        ], true));
      } else {
        list.replaceChildren(emptyState(t("list.loadFailed"), e.message, [
          el("button", { type: "button", class: "btn", text: t("action.retry"), onclick: loadList }),
        ], true));
      }
      return;
    }
    if (state.listCtrl !== ctrl) return;
    state.listCtrl = null;
    list.removeAttribute("aria-busy");
    if (state.mode === "search") hideSearchError();
    state.total = data.total ?? null;
    state.more = data.more ?? (state.offset + data.items.length < (data.total || 0));
    renderList(data.items, data.terms || []);
  }

  function openHelp() {
    const panel = $("search-help");
    if (panel.hidden) $("help-btn").click();
    else panel.scrollIntoView({ block: "nearest" });
  }

  /** Teilt den Betreff an den Suchwörtern und markiert Treffer mit &lt;mark&gt; – nur Textknoten. */
  function highlighted(text, terms) {
    const frag = document.createDocumentFragment();
    if (!terms.length) { frag.append(text); return frag; }
    const escaped = terms.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).sort((a, b) => b.length - a.length);
    const re = new RegExp("(" + escaped.join("|") + ")", "giu");
    let last = 0;
    for (const m of text.matchAll(re)) {
      if (m.index > last) frag.append(text.slice(last, m.index));
      frag.append(el("mark", { text: m[0] }));
      last = m.index + m[0].length;
    }
    if (last < text.length) frag.append(text.slice(last));
    return frag;
  }

  function renderList(items, terms) {
    $("list-count").dataset.mode = state.mode;
    const list = $("message-list");
    const searching = state.mode === "search";
    list.replaceChildren();

    if (searching) {
      $("list-count").textContent = items.length === 0 ? "" :
        t(state.more ? "count.hitsMore" : "count.hits", { count: nf.format(items.length) });
    } else {
      $("list-count").textContent = state.total != null ? t("count.mails", { count: nf.format(state.total) }) : "";
    }

    if (items.length === 0) {
      if (searching) {
        const acts = [];
        if (filtersActive()) acts.push(el("button", { type: "button", class: "btn", text: t("action.resetFilters"), onclick: () => { resetFilters(); runSearch(); } }));
        if (state.filters.scope) acts.push(el("button", { type: "button", class: "btn", text: t("action.searchAll"), onclick: () => { $("scope-chip").click(); } }));
        acts.push(el("button", { type: "button", class: "btn btn-quiet", text: t("help.button"), onclick: openHelp }));
        list.append(emptyState(
          state.lastQuery ? t("empty.noHitsFor", { query: state.lastQuery }) : t("empty.noMatches"),
          t("empty.noHitsText"), acts));
      } else {
        const acts = [];
        if (state.folderHasChildren && !$("recursive").checked) {
          acts.push(el("button", { type: "button", class: "btn", text: t("action.includeSub"), onclick: () => { $("recursive").checked = true; loadList(); } }));
        }
        list.append(emptyState(t("empty.folderTitle"),
          state.folderHasChildren ? t("empty.folderSub") : t("empty.folderText"), acts));
      }
    }

    for (const m of items) {
      const name = senderName(m) || t("msg.unknownSender");
      const subjText = m.subject || t("msg.noSubject");
      const subject = el("span", { class: "msg-subject" + (m.subject ? "" : " none") },
        el("span", { class: "msg-subject-text" }, m.subject && searching ? highlighted(subjText, terms) : subjText));
      const row = el("button", { type: "button", class: "msg", role: "listitem" },
        el("span", { class: "msg-from", text: name, title: m.fromAddress || m.from || null }),
        el("span", { class: "msg-date", text: formatListDate(m.date) }),
        subject,
        m.hasAttachments ? icon("clip", "clip", t("msg.withAttachment")) : null,
        searching && m.folderPath ? el("span", { class: "msg-folder", text: m.folderPath }) : null);
      row.dataset.id = String(m.id);
      if (state.activeMessage === m.id) row.classList.add("active");
      row.addEventListener("click", () => openMessage(m.id, row));
      list.append(row);
    }
    list.scrollTop = 0;

    const pager = $("pager");
    if (searching || (state.offset === 0 && !state.more)) { pager.hidden = true; return; }
    pager.hidden = false;
    const from = items.length ? state.offset + 1 : 0;
    const to = state.offset + items.length;
    $("page-info").textContent = state.total != null
      ? t("pager.range", { from: nf.format(from), to: nf.format(to), total: nf.format(state.total) })
      : (items.length ? `${nf.format(from)}–${nf.format(to)}` : "");
    $("prev-btn").disabled = state.offset === 0;
    $("next-btn").disabled = !state.more;
  }

  // Pfeiltasten in der Liste
  $("message-list").addEventListener("keydown", (ev) => {
    if (ev.key !== "ArrowDown" && ev.key !== "ArrowUp") return;
    const row = ev.target.closest(".msg");
    if (!row) return;
    const next = ev.key === "ArrowDown" ? row.nextElementSibling : row.previousElementSibling;
    if (next && next.classList.contains("msg")) { ev.preventDefault(); next.focus(); }
  });

  $("prev-btn").addEventListener("click", () => { state.offset = Math.max(0, state.offset - PAGE_SIZE); loadList(); });
  $("next-btn").addEventListener("click", () => { state.offset += PAGE_SIZE; loadList(); });

  // ---------------------------------------------------------------- Lesebereich

  function clearReader() {
    state.activeMessage = null;
    $("reader").hidden = true;
    $("reader-empty").hidden = false;
    $("r-body").removeAttribute("src");
  }

  function setReaderEmpty(title, text) {
    const box = $("reader-empty");
    box.replaceChildren(el("p", { class: "empty-title", text: title }), el("p", { class: "empty-text", text }));
  }

  async function openMessage(id, row) {
    document.querySelectorAll(".msg.active").forEach((b) => b.classList.remove("active"));
    if (row) row.classList.add("active");
    state.activeMessage = id;
    if (state.msgCtrl) state.msgCtrl.abort();
    const ctrl = new AbortController();
    state.msgCtrl = ctrl;
    let m;
    try { m = await api(`/api/messages/${encodeURIComponent(id)}`, { signal: ctrl.signal }); } catch (e) {
      if (isAbort(e) || e instanceof AuthError) return;
      clearReader();
      setReaderEmpty(t("reader.notOpened"), e.status === 404 ? t("reader.gone") : e.message);
      setView("reader");
      return;
    }
    if (state.activeMessage !== id) return;
    $("r-subject").textContent = m.subject || t("msg.noSubject");
    $("r-from").textContent = m.from;
    $("r-to").textContent = m.to;
    $("r-cc").textContent = m.cc;
    $("r-cc").hidden = $("r-cc-label").hidden = !m.cc;
    const d = new Date(m.date);
    $("r-date").textContent = isNaN(d) ? "" : dateLong.format(d);
    $("r-folder").textContent = m.folderPath;
    $("r-eml").href = `/api/messages/${encodeURIComponent(id)}/eml`;

    const atts = $("r-attachments");
    atts.replaceChildren();
    for (const a of m.attachments) {
      const link = el("a", { href: `/api/messages/${encodeURIComponent(id)}/attachments/${a.index}`, download: "", title: t("reader.attDownload", { name: a.name }) },
        icon("clip"), el("span", { class: "att-name", text: a.name }), el("span", { class: "att-size", text: formatSize(a.size) }));
      atts.append(el("li", null, link));
    }
    atts.hidden = m.attachments.length === 0;

    $("r-body").src = `/api/messages/${encodeURIComponent(id)}/body`;
    $("reader-empty").hidden = true;
    $("reader").hidden = false;
    $("reader").scrollTop = 0;
    setView("reader");
  }

  $("back-btn").addEventListener("click", () => {
    setView("list");
    const row = document.querySelector(`.msg[data-id="${CSS.escape(String(state.activeMessage))}"]`);
    if (row) row.focus({ preventScroll: false });
  });

  // ---------------------------------------------------------------- Start

  (async () => {
    try {
      const me = await api("/api/me");
      await showApp(me);
    } catch (e) {
      if (!(e instanceof AuthError)) showLogin();
    }
  })();
})();
