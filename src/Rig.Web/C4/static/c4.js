// C4 zoom viewer. Pele (vendored, global `pele`) draws each concept's Mermaid in the browser; the server only
// serves the bundle as JSON. Clicking an element with a $link zooms into the view it links to.

const $ = (id) => document.getElementById(id);
const DIAGRAM_FONT_SIZE = 20;
const MAX_UPSCALE = 1.4;
const state = { bundle: null, concept: null, path: [], expanded: null, zoom: null, drawer: null };

// Each C4 level, the name shown on its pill, and the hue of the elements it is about (matches c4.css).
const LEVELS = {
  "C4 Context": { n: 1, name: "System context", hue: "--c4-system" },
  "C4 Container": { n: 2, name: "Containers", hue: "--c4-container" },
  "C4 Component": { n: 3, name: "Components", hue: "--c4-component" },
  "C4 Code": { n: 4, name: "Code", hue: "--c4-code" },
};
const level = (concept) => (concept && LEVELS[concept.type]) || null;
const hueOf = (id) => {
  const concept = state.bundle && state.bundle.concepts.find((c) => c.id === id);
  const found = concept && LEVELS[concept.type];
  return found ? `var(${found.hue})` : null;
};

// The bundle is chosen by ?bundle= on the page and passed to every API call; the hash is the concept.
const BUNDLE = new URLSearchParams(location.search).get("bundle");

async function api(endpoint, params = {}) {
  const query = new URLSearchParams(BUNDLE ? { ...params, bundle: BUNDLE } : params).toString();
  const response = await fetch(`/api/c4/${endpoint}${query ? `?${query}` : ""}`);
  const body = await response.json();
  if (!response.ok) throw new Error(body.error || response.statusText);
  return body;
}

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (value !== undefined && value !== null && value !== false) node.setAttribute(key, value);
  }
  node.append(...children.filter((c) => c !== null && c !== undefined));
  return node;
}

// Mirrors bundle.resolve_href: bundle-absolute with a leading "/", else relative to the linking concept.
function resolveHref(fromId, href) {
  if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith("#")) return null;
  const clean = href.split("#")[0];
  const base = clean.startsWith("/") ? [] : fromId.split("/").slice(0, -1);
  const parts = [...base];
  for (const segment of clean.replace(/^\//, "").split("/")) {
    if (segment === "" || segment === ".") continue;
    if (segment === "..") {
      if (!parts.length) return null;
      parts.pop();
    } else parts.push(segment);
  }
  return parts.join("/").replace(/\.md$/, "") || null;
}

// The first path from a root to `id` in the zoom tree, so deep links and tree clicks get a breadcrumb.
function treePath(id) {
  const walk = (nodes, trail) => {
    for (const node of nodes) {
      const next = [...trail, { id: node.id, title: node.title, via: node.via }];
      if (node.id === id) return next;
      const found = walk(node.children, next);
      if (found) return found;
    }
    return null;
  };
  const concept = state.bundle.concepts.find((c) => c.id === id);
  return walk(state.bundle.tree, []) || [{ id, title: concept ? concept.title : id, via: null }];
}

function renderCrumbs() {
  const nav = $("crumbs");
  nav.replaceChildren();
  state.path.forEach((step, index) => {
    if (index) nav.append(el("span", { class: "sep" }, "›"));
    const current = index === state.path.length - 1;
    const button = el(
      "button",
      { "aria-current": current ? "page" : null, title: step.title, onclick: () => !current && goBack(index) },
      el("span", { class: "dot" }),
      step.title,
    );
    const hue = hueOf(step.id);
    if (hue) button.style.setProperty("--hue", hue);
    nav.append(button);
  });
}

function renderTree() {
  const build = (nodes) =>
    el(
      "ul",
      {},
      ...nodes.map((node) =>
        el(
          "li",
          {},
          (() => {
            const button = el(
              "button",
              { "aria-current": state.concept && node.id === state.concept.id ? "true" : null, onclick: () => open(node.id) },
              el("span", { class: "dot" }),
              el("span", {}, node.title, node.repeat ? el("span", { class: "repeat" }, " (again)") : null),
            );
            const hue = hueOf(node.id);
            if (hue) button.style.setProperty("--hue", hue);
            return button;
          })(),
          node.children.length && !node.repeat ? build(node.children) : null,
        ),
      ),
    );
  $("tree").replaceChildren(...build(state.bundle.tree).children);
}

function renderFindings() {
  const { findings, counts } = state.bundle;
  const button = $("counts");
  button.title = `${state.bundle.concepts.length} views · ${counts.error} errors · ${counts.warning} warnings · ${counts.info} info`;
  button.replaceChildren(
    ...(counts.error || counts.warning
      ? [
          counts.error ? el("span", { class: "n-error" }, `${counts.error} ✕ `) : null,
          counts.warning ? el("span", { class: "n-warning" }, `${counts.warning} ⚠ `) : null,
          "Findings",
        ].filter(Boolean)
      : [el("span", { class: "n-ok" }, "✓ "), "Checks pass"]),
  );
  $("findings").replaceChildren(
    ...findings.map((f) =>
      el(
        "li",
        { class: f.severity },
        el("span", { class: "sev" }, f.severity),
        el("span", { class: "code" }, f.code),
        " ",
        el("button", { class: "c4-link", onclick: () => open(f.concept) }, f.concept),
        el("div", {}, f.message),
      ),
    ),
  );
}

function renderSide(concept) {
  const lvl = level(concept);
  const pill = $("concept-level");
  pill.replaceChildren(
    ...(lvl ? [el("span", { class: "n" }, `Level ${lvl.n}`), " · ", lvl.name] : [concept.type || concept.keyword || "Concept"]),
  );
  pill.style.setProperty("--hue", lvl ? `var(${lvl.hue})` : "");
  document.title = `${concept.title} · C4`;
  $("concept-title").textContent = concept.title;
  $("concept-description").textContent = concept.description || "";
  $("notes").innerHTML = concept.notes_html || "<p><em>No notes.</em></p>";
  $("element-note").hidden = true;

  $("inbound").replaceChildren(
    ...(concept.inbound.length
      ? concept.inbound.map((link) =>
          el(
            "li",
            {},
            el("button", { onclick: () => open(link.source) }, link.title),
            el("span", { class: "kind" }, ` ${link.kind}${link.alias ? ` via ${link.alias}` : ""}`),
          ),
        )
      : [el("li", { class: "kind" }, "nothing links here")]),
  );

  const prov = $("provenance");
  prov.replaceChildren(
    el("dt", {}, "id"),
    el("dd", {}, concept.id),
    el("dt", {}, "generated"),
    el("dd", {}, concept.generated ? `${concept.generated.by} · ${concept.generated.at}` : "unattributed"),
    el("dt", {}, "verified"),
    concept.verified
      ? el("dd", {}, `${concept.verified.by} · ${concept.verified.at}`)
      : el("dd", { class: "unverified" }, "not verified"),
    ...(concept.resource ? [el("dt", {}, "resource"), el("dd", {}, el("a", { href: concept.resource }, concept.resource))] : []),
  );

  const expandable = concept.zooms.filter((z) => z.expandable && z.kind === "zoom");
  $("expand-controls").replaceChildren(
    ...(expandable.length ? [el("span", { class: "lead" }, "Open in place:")] : []),
    ...expandable.map((z) =>
      el(
        "button",
        { "aria-pressed": state.expanded === z.alias ? "true" : "false", title: `Draw ${z.title} inside this view`, onclick: () => toggleExpand(z.alias) },
        `${state.expanded === z.alias ? "▾" : "▸"} ${labelOf(z.alias)}`,
      ),
    ),
  );
  decorateNotes();
}

function showElementNote(alias) {
  const box = $("element-note");
  const note = state.concept.element_notes[alias];
  const zoom = state.concept.zooms.find((z) => z.alias === alias);
  const label = labelOf(alias);
  box.replaceChildren(el("h3", {}, label, label !== alias ? el("span", { class: "alias" }, alias) : null));
  if (note) box.insertAdjacentHTML("beforeend", note);
  else box.append(el("p", {}, el("em", {}, "No note for this element.")));
  if (zoom) box.append(el("button", { class: "zoom", onclick: () => zoomInto(alias, zoom.target) }, `Zoom in → ${zoom.title}`));
  const node = nodeFor(alias);
  box.style.setProperty("--hue", node ? getComputedStyle(node).getPropertyValue("--hue").trim() : "");
  box.hidden = false;
  $("inspector").scrollTop = 0;
}

// The rendered element for an alias, and its label as drawn (falls back to the alias before a draw).
const nodeFor = (alias) =>
  [...$("stage").querySelectorAll("g.pele-node[data-id]")].find((n) => n.getAttribute("data-id") === alias) || null;
function labelOf(alias) {
  const node = nodeFor(alias);
  const label = node && (node.querySelector(".pele-label") || node.querySelector("text:not(.pele-c4-type)"));
  const lines = label ? [...label.querySelectorAll("tspan")].map((t) => t.textContent.trim()) : [];
  const text = label ? (lines.length ? lines.join(" ") : label.textContent).trim() : "";
  return text || alias;
}

// Notes are written under "## alias" headings; show the element's label with its colour, and the alias small.
function decorateNotes() {
  for (const heading of $("notes").querySelectorAll("h2")) {
    const alias = heading.dataset.alias || heading.textContent.trim();
    heading.dataset.alias = alias;
    const node = nodeFor(alias);
    if (!node) continue;
    const label = labelOf(alias);
    heading.replaceChildren(label, label !== alias ? el("span", { class: "alias" }, alias) : "");
    heading.style.setProperty("--hue", getComputedStyle(node).getPropertyValue("--hue").trim());
    heading.style.cursor = "pointer";
    heading.onclick = () => selectNode(alias);
  }
}

function selectNode(alias) {
  document.querySelectorAll(".c4-selected").forEach((n) => n.classList.remove("c4-selected"));
  const node = nodeFor(alias);
  if (node) node.classList.add("c4-selected");
  showElementNote(alias);
}

function draw(source, { arriving = false } = {}) {
  const stage = $("stage");
  if (state.zoom) state.zoom.destroy();
  state.zoom = null;
  const canvas = el("div", { class: arriving ? "c4-canvas arriving" : "c4-canvas" });
  try {
    // Measure text in the font the theme will draw it in, or labels overflow their boxes.
    const fontFamily = getComputedStyle(stage).getPropertyValue("--pele-font").trim() || undefined;
    // Pele keeps box widths and scales the text inside them, so a larger size fills each box rather than
    // growing the map. 20 draws labels at about 17px and descriptions at 16px before the fit to the stage.
    const result = pele.render(source, {
      responsive: true, links: true, linkSchemes: ["https", "http"], fontFamily, fontSize: DIAGRAM_FONT_SIZE,
    });
    canvas.innerHTML = result.svg;
    const svg = canvas.querySelector("svg");
    if (svg) {
      svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
      // Fill the stage, but never blow a small diagram (one class, two boxes) up past MAX_UPSCALE.
      const [, , width, height] = (svg.getAttribute("viewBox") || "").split(/\s+/).map(Number);
      if (width && height) {
        svg.style.setProperty("max-width", `${width * MAX_UPSCALE}px`, "important");
        svg.style.setProperty("max-height", `${height * MAX_UPSCALE}px`, "important");
      }
    }
  } catch (error) {
    canvas.append(el("div", { class: "c4-error-box" }, `Pele could not render this diagram:\n${error.message}`));
  }
  stage.replaceChildren(canvas);
  if (arriving) requestAnimationFrame(() => requestAnimationFrame(() => canvas.classList.remove("arriving")));
  if (canvas.querySelector("svg")) state.zoom = pele.enableZoom(canvas, { controls: true });
  decorateNotes();
}

function onStageClick(event) {
  const anchor = event.target.closest("a");
  const node = event.target.closest("[data-id]");
  const alias = node ? node.getAttribute("data-id") : null;
  if (anchor) {
    event.preventDefault();
    const href = anchor.getAttribute("href") || anchor.getAttribute("xlink:href") || "";
    const target = resolveHref(state.concept.id, href);
    if (target === null) return window.open(href, "_blank", "noopener");
    return zoomInto(alias, target, node);
  }
  if (!alias || node.classList.contains("pele-edge") || node.classList.contains("pele-edge-label")) return;
  selectNode(alias);
}

async function zoomInto(alias, target, node) {
  const canvas = $("stage").querySelector(".c4-canvas");
  if (node && canvas) {
    // Fly into the element: scale the canvas so the element's box fills the stage, fading as it goes.
    const stageBox = $("stage").getBoundingClientRect();
    const box = node.getBoundingClientRect();
    const scale = Math.min(stageBox.width / Math.max(box.width, 1), stageBox.height / Math.max(box.height, 1), 6);
    const dx = box.left - stageBox.left;
    const dy = box.top - stageBox.top;
    canvas.style.transform = `scale(${scale}) translate(${-dx}px, ${-dy}px)`;
    canvas.classList.add("leaving");
    await new Promise((resolve) => setTimeout(resolve, 380));
  }
  const step = state.path[state.path.length - 1];
  const isBack = state.path.length > 1 && state.path[state.path.length - 2].id === target;
  const path = isBack ? state.path.slice(0, -1) : [...state.path, { id: target, title: target, via: alias }];
  if (!step) return open(target);
  await load(target, path, { arriving: true });
}

async function load(id, path, { arriving = false } = {}) {
  let concept;
  try {
    concept = await api("concept", { id });
  } catch (error) {
    $("stage").replaceChildren(el("div", { class: "c4-error-box" }, error.message));
    return;
  }
  state.concept = concept;
  state.expanded = null;
  state.path = path.map((step) => (step.id === id ? { ...step, title: concept.title } : step));
  if (location.hash.slice(1) !== id) history.pushState(null, "", `#${id}`);
  renderCrumbs();
  renderTree();
  if (concept.diagram) draw(concept.diagram, { arriving });
  else $("stage").replaceChildren(el("div", { class: "c4-error-box" }, "This concept has no diagram — see its notes."));
  renderSide(concept);
}

function open(id) {
  return load(id, treePath(id));
}

function goBack(index) {
  const step = state.path[index];
  return load(step.id, state.path.slice(0, index + 1), { arriving: true });
}

async function toggleExpand(alias) {
  if (state.expanded === alias) {
    state.expanded = null;
    draw(state.concept.diagram);
  } else {
    try {
      const view = await api("expand", { id: state.concept.id, alias });
      state.expanded = alias;
      draw(view.diagram, { arriving: true });
    } catch (error) {
      $("stage").replaceChildren(el("div", { class: "c4-error-box" }, error.message));
    }
  }
  renderSide(state.concept);
}

// Links in notes are OKF links to other concepts: open them in the viewer instead of fetching raw markdown.
function onNotesClick(event) {
  const anchor = event.target.closest("a[href]");
  if (!anchor || !state.concept) return;
  const target = resolveHref(state.concept.id, anchor.getAttribute("href"));
  if (target === null) return;
  event.preventDefault();
  open(target);
}

// The drawer holds one section at a time; the inspector is its own column. Both just toggle classes on the grid.
function setDrawer(which) {
  state.drawer = state.drawer === which ? null : which;
  $("drawer").hidden = !state.drawer;
  $("drawer-tree").hidden = state.drawer !== "tree";
  $("drawer-findings").hidden = state.drawer !== "findings";
  $("layout").classList.toggle("with-drawer", Boolean(state.drawer));
  $("toggle-tree").setAttribute("aria-pressed", String(state.drawer === "tree"));
  $("counts").setAttribute("aria-pressed", String(state.drawer === "findings"));
}

function toggleNotes() {
  const hidden = $("layout").classList.toggle("no-inspector");
  $("toggle-notes").setAttribute("aria-pressed", String(!hidden));
}

async function boot() {
  $("toggle-tree").addEventListener("click", () => setDrawer("tree"));
  $("counts").addEventListener("click", () => setDrawer("findings"));
  $("toggle-notes").addEventListener("click", toggleNotes);
  $("stage").addEventListener("click", onStageClick);
  $("notes").addEventListener("click", onNotesClick);
  $("element-note").addEventListener("click", onNotesClick);
  window.addEventListener("popstate", () => {
    const id = location.hash.slice(1);
    if (id && (!state.concept || id !== state.concept.id)) open(id);
  });
  // Pele measures text when it lays out, so the face must be loaded first or labels overflow their boxes.
  await Promise.race([document.fonts.load('600 16px "Inter Variable"'), new Promise((r) => setTimeout(r, 1500))]).catch(() => {});
  try {
    state.bundle = await api("bundle");
  } catch (error) {
    $("stage").replaceChildren(el("div", { class: "c4-error-box" }, `Could not load the bundle: ${error.message}`));
    return;
  }
  renderBundles();
  renderFindings();
  const first = location.hash.slice(1) || (state.bundle.tree[0] && state.bundle.tree[0].id);
  if (first) await open(first);
}

function renderBundles() {
  const select = el("select", {
    "aria-label": "Bundle",
    onchange: (event) => { location.href = `/c4/?bundle=${encodeURIComponent(event.target.value)}`; },
  }, ...state.bundle.bundles.map((name) => el("option", { value: name, selected: name === state.bundle.name }, name)));
  $("bundle-name").replaceChildren(select);
}

boot();
