const repositorySelect = document.getElementById("repositorySelect");
const summaryGrid = document.getElementById("summaryGrid");
const repositoryMeta = document.getElementById("repositoryMeta");
const nodeList = document.getElementById("nodeList");
const searchInput = document.getElementById("searchInput");
const searchButton = document.getElementById("searchButton");
const rebuildButton = document.getElementById("rebuildButton");
const statusBadge = document.getElementById("statusBadge");

const questionInput = document.getElementById("questionInput");
const intentSelect = document.getElementById("intentSelect");
const graphDepthInput = document.getElementById("graphDepthInput");
const limitInput = document.getElementById("limitInput");
const askButton = document.getElementById("askButton");
const browseButton = document.getElementById("browseButton");

const answerSummary = document.getElementById("answerSummary");
const strategyChip = document.getElementById("strategyChip");
const queryMeta = document.getElementById("queryMeta");
const editPlanView = document.getElementById("editPlanView");
const evidenceList = document.getElementById("evidenceList");

const contextSummary = document.getElementById("contextSummary");
const filesList = document.getElementById("filesList");
const symbolsList = document.getElementById("symbolsList");
const neighborsList = document.getElementById("neighborsList");

const sourceMeta = document.getElementById("sourceMeta");
const sourceView = document.getElementById("sourceView");
const contextCards = document.getElementById("contextCards");
const impactCards = document.getElementById("impactCards");

let activeRepositoryId = null;
let repositoriesCache = [];

async function getJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return await response.json();
}

function setStatus(text, tone = "idle") {
  statusBadge.textContent = text;
  statusBadge.dataset.tone = tone;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;");
}

function renderSummary(summary) {
  const items = [
    ["Nodes", summary.nodeCount ?? 0],
    ["Edges", summary.edgeCount ?? 0],
    ["Projects", summary.projectCount ?? 0],
    ["Symbols", summary.symbolCount ?? 0],
    ["Cards", summary.understandingCardCount ?? 0],
  ];

  summaryGrid.innerHTML = items.map(([label, value]) => `
    <div class="stat">
      <span class="label">${label}</span>
      <span class="value">${value}</span>
    </div>
  `).join("");
}

function renderRepositoryMeta() {
  const repository = repositoriesCache.find(item => item.id === activeRepositoryId);
  if (!repository) {
    repositoryMeta.innerHTML = `<div class="emptyState inline">No repository selected.</div>`;
    return;
  }

  repositoryMeta.innerHTML = `
    <div class="metaPair"><span>Analyzer</span><strong>${escapeHtml(repository.analyzerId)}</strong></div>
    <div class="metaPair"><span>Language</span><strong>${escapeHtml(repository.primaryLanguage)}</strong></div>
    <div class="metaPair"><span>Indexed</span><strong>${repository.lastIndexedAt ? new Date(repository.lastIndexedAt).toLocaleString() : "Not yet"}</strong></div>
    <div class="metaPair wide"><span>Path</span><strong>${escapeHtml(repository.rootPath)}</strong></div>
  `;
}

function renderRepositories(repositories) {
  repositoriesCache = repositories;
  repositorySelect.innerHTML = repositories.map(repo => `
    <option value="${repo.id}">${repo.displayName} (${repo.id})</option>
  `).join("");

  activeRepositoryId = repositories[0]?.id ?? null;
  repositorySelect.value = activeRepositoryId ?? "";
  renderRepositoryMeta();
}

function renderNodes(nodes) {
  if (!nodes.length) {
    nodeList.innerHTML = `<div class="emptyState listEmpty">No matching nodes.</div>`;
    return;
  }

  nodeList.innerHTML = nodes.map(node => `
    <button class="listItem" data-symbol="${encodeURIComponent(node.symbol ?? "")}" data-file="${encodeURIComponent(node.filePath ?? "")}">
      <strong>${escapeHtml(node.label || "(unnamed)")}</strong>
      <div class="meta">${escapeHtml(node.kind)}${node.symbol ? ` | ${escapeHtml(node.symbol)}` : ""}</div>
      <div class="meta">${escapeHtml(node.filePath ?? node.namespace ?? "")}</div>
    </button>
  `).join("");

  for (const element of nodeList.querySelectorAll(".listItem")) {
    element.addEventListener("click", async () => {
      const symbol = decodeURIComponent(element.dataset.symbol || "");
      const filePath = decodeURIComponent(element.dataset.file || "");
      await inspectSelection({ symbol, filePath });
    });
  }
}

function renderContextBundle(context) {
  contextSummary.innerHTML = context?.summary
    ? `<div class="contextCallout">${escapeHtml(context.summary)}</div>`
    : `<div class="emptyState inline">No assembled context.</div>`;

  renderPillList(filesList, context?.files, "No files selected.");
  renderPillList(symbolsList, context?.symbols, "No symbols selected.");
  renderPillList(neighborsList, context?.graphNeighbors, "No graph neighbors.");
}

function renderPillList(element, items, emptyText) {
  if (!items?.length) {
    element.innerHTML = `<div class="emptyState inline">${escapeHtml(emptyText)}</div>`;
    return;
  }

  element.innerHTML = items.map(item => `<button class="pill" data-value="${encodeURIComponent(item)}">${escapeHtml(item)}</button>`).join("");
  for (const button of element.querySelectorAll(".pill")) {
    button.addEventListener("click", async () => {
      const value = decodeURIComponent(button.dataset.value || "");
      await inspectSelection({ symbol: value, filePath: value.includes(":\\") ? value : "" });
    });
  }
}

function renderAnswer(result) {
  const hits = result.hits ?? [];
  answerSummary.innerHTML = `
    <div class="answerLead">
      <h3>${escapeHtml(result.queryText)}</h3>
      <p>${escapeHtml(result.context?.summary || "No context summary available.")}</p>
    </div>
  `;
  strategyChip.textContent = result.retrievalStrategy || "Hybrid Retrieval";
  queryMeta.textContent = `${hits.length} evidence hits · intent ${result.intent}`;

  renderEditPlan(result.suggestedEditPlan);
  renderEvidence(hits);
  renderContextBundle(result.context);
}

function renderEditPlan(plan) {
  if (!plan) {
    editPlanView.innerHTML = `<div class="emptyState inline">No edit plan available.</div>`;
    return;
  }

  editPlanView.innerHTML = `
    <div class="planCard">
      <div class="planGoal">${escapeHtml(plan.goal)}</div>
      <div class="planGrid">
        <div>
          <span class="sectionLabel">Files To Inspect</span>
          <ul>${(plan.filesToInspect || []).slice(0, 6).map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul>
        </div>
        <div>
          <span class="sectionLabel">Symbols To Edit</span>
          <ul>${(plan.symbolsToEdit || []).slice(0, 6).map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul>
        </div>
        <div>
          <span class="sectionLabel">Verification</span>
          <ul>${(plan.verificationSteps || []).slice(0, 6).map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul>
        </div>
      </div>
      <div class="riskCallout">${escapeHtml(plan.riskNotes || "No risk notes.")}</div>
    </div>
  `;
}

function renderEvidence(hits) {
  if (!hits.length) {
    evidenceList.innerHTML = `<div class="emptyState inline">No evidence returned.</div>`;
    return;
  }

  evidenceList.innerHTML = hits.map((hit, index) => `
    <article class="evidenceCard" data-symbol="${encodeURIComponent(hit.symbol ?? "")}" data-file="${encodeURIComponent(hit.filePath ?? "")}">
      <div class="evidenceTop">
        <div>
          <span class="evidenceIndex">#${index + 1}</span>
          <h4>${escapeHtml(hit.title || "(untitled hit)")}</h4>
        </div>
        <div class="scoreBadge">${Number(hit.score ?? 0).toFixed(2)}</div>
      </div>
      <p class="evidenceContent">${escapeHtml(hit.content || "")}</p>
      <div class="scoreBreakdown">
        <span>BM25 ${Number(hit.bm25Score ?? 0).toFixed(2)}</span>
        <span>Vector ${Number(hit.vectorScore ?? 0).toFixed(2)}</span>
        <span>Graph ${Number(hit.graphScore ?? 0).toFixed(2)}</span>
      </div>
      <div class="evidenceMeta">${escapeHtml(hit.filePath || hit.symbol || "")}</div>
    </article>
  `).join("");

  for (const card of evidenceList.querySelectorAll(".evidenceCard")) {
    card.addEventListener("click", async () => {
      const symbol = decodeURIComponent(card.dataset.symbol || "");
      const filePath = decodeURIComponent(card.dataset.file || "");
      await inspectSelection({ symbol, filePath });
    });
  }
}

function renderSourceSnippet(snippet) {
  if (!snippet) {
    sourceMeta.textContent = "No source loaded.";
    sourceView.textContent = "No source loaded.";
    return;
  }

  sourceMeta.textContent = `${snippet.filePath} · lines ${snippet.startLine}-${snippet.endLine}`;
  sourceView.textContent = snippet.content;
}

function renderContextDetails(context) {
  if (!context) {
    contextCards.innerHTML = `<div class="emptyState inline">No context loaded.</div>`;
    return;
  }

  const relatedNodes = (context.relatedNodes || []).slice(0, 12);
  const outgoingEdges = (context.outgoingEdges || []).slice(0, 12);
  contextCards.innerHTML = `
    <div class="detailCard">
      <div class="detailHeader">${escapeHtml(context.symbol || "Unknown symbol")}</div>
      <div class="detailSection">
        <span class="sectionLabel">Related Nodes</span>
        <ul>${relatedNodes.map(node => `<li>${escapeHtml(node.symbol || node.label)}</li>`).join("")}</ul>
      </div>
      <div class="detailSection">
        <span class="sectionLabel">Outgoing Edges</span>
        <ul>${outgoingEdges.map(edge => `<li>${escapeHtml(edge.kind)} → ${escapeHtml(edge.to)}</li>`).join("")}</ul>
      </div>
    </div>
  `;
}

function renderImpactDetails(impact) {
  if (!impact) {
    impactCards.innerHTML = `<div class="emptyState inline">No impact loaded.</div>`;
    return;
  }

  impactCards.innerHTML = `
    <div class="detailCard">
      <div class="detailHeader">${escapeHtml(impact.symbol || "Unknown symbol")}</div>
      <div class="detailSection">
        <span class="sectionLabel">Impacted Nodes</span>
        <ul>${(impact.impactedNodes || []).slice(0, 16).map(node => `<li>${escapeHtml(node.symbol || node.label)}</li>`).join("")}</ul>
      </div>
      <div class="detailSection">
        <span class="sectionLabel">Traversed Edges</span>
        <ul>${(impact.traversedEdges || []).slice(0, 16).map(edge => `<li>${escapeHtml(edge.kind)}: ${escapeHtml(edge.from)} → ${escapeHtml(edge.to)}</li>`).join("")}</ul>
      </div>
    </div>
  `;
}

async function loadRepositories() {
  const repositories = await getJson("/api/repos");
  renderRepositories(repositories);
}

async function loadSummary() {
  if (!activeRepositoryId) {
    renderSummary({});
    return;
  }

  const summary = await getJson(`/api/graph/summary?repositoryId=${encodeURIComponent(activeRepositoryId)}`);
  renderSummary(summary);
  renderRepositoryMeta();
}

async function loadNodes(term = "") {
  if (!activeRepositoryId) {
    renderNodes([]);
    return;
  }

  const nodes = await getJson(`/api/graph/nodes?repositoryId=${encodeURIComponent(activeRepositoryId)}&term=${encodeURIComponent(term)}&limit=50`);
  renderNodes(nodes);
}

async function runQuery() {
  if (!activeRepositoryId || !questionInput.value.trim()) {
    return;
  }

  setStatus("Querying repository", "working");
  askButton.disabled = true;
  try {
    const result = await getJson("/api/query", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        query: questionInput.value.trim(),
        repositories: [activeRepositoryId],
        intent: intentSelect.value,
        limit: Number(limitInput.value || 8),
        graphDepth: Number(graphDepthInput.value || 2),
      }),
    });

    renderAnswer(result);
    if (result.hits?.length) {
      const lead = result.hits[0];
      await inspectSelection({ symbol: lead.symbol || "", filePath: lead.filePath || "" });
    }
    setStatus("Answer ready", "success");
  } catch (error) {
    answerSummary.innerHTML = `<div class="emptyState inline">Query failed: ${escapeHtml(error.message)}</div>`;
    setStatus("Query failed", "error");
  } finally {
    askButton.disabled = false;
  }
}

async function inspectSelection({ symbol, filePath }) {
  if (!activeRepositoryId) {
    return;
  }

  setStatus("Loading evidence", "working");
  switchTab("source");

  const requests = [];
  if (symbol || filePath) {
    requests.push(loadSource(symbol, filePath));
  }
  if (symbol) {
    requests.push(loadContext(symbol));
    requests.push(loadImpact(symbol));
  } else {
    renderContextDetails(null);
    renderImpactDetails(null);
  }

  await Promise.all(requests);
  setStatus("Selection loaded", "success");
}

async function loadSource(symbol, filePath) {
  try {
    const snippet = await getJson(`/api/source/snippet?repositoryId=${encodeURIComponent(activeRepositoryId)}&symbol=${encodeURIComponent(symbol || "")}&filePath=${encodeURIComponent(filePath || "")}`);
    renderSourceSnippet(snippet);
  } catch {
    renderSourceSnippet(null);
  }
}

async function loadContext(symbol) {
  try {
    const context = await getJson(`/api/graph/context?repositoryId=${encodeURIComponent(activeRepositoryId)}&symbol=${encodeURIComponent(symbol)}`);
    renderContextDetails(context);
  } catch {
    renderContextDetails(null);
  }
}

async function loadImpact(symbol) {
  try {
    const impact = await getJson(`/api/graph/impact?repositoryId=${encodeURIComponent(activeRepositoryId)}&symbol=${encodeURIComponent(symbol)}&depth=${encodeURIComponent(graphDepthInput.value || "2")}&limit=24`);
    renderImpactDetails(impact);
  } catch {
    renderImpactDetails(null);
  }
}

function switchTab(tabName) {
  for (const tab of document.querySelectorAll(".tab")) {
    tab.classList.toggle("active", tab.dataset.tab === tabName);
  }

  for (const panel of document.querySelectorAll(".tabPanel")) {
    panel.classList.toggle("active", panel.id === `${tabName}Panel`);
  }
}

repositorySelect.addEventListener("change", async () => {
  activeRepositoryId = repositorySelect.value || null;
  renderRepositoryMeta();
  await loadSummary();
  await loadNodes(searchInput.value.trim());
});

searchButton.addEventListener("click", () => loadNodes(searchInput.value.trim()));
searchInput.addEventListener("keydown", event => {
  if (event.key === "Enter") {
    loadNodes(searchInput.value.trim());
  }
});

askButton.addEventListener("click", runQuery);
questionInput.addEventListener("keydown", event => {
  if ((event.metaKey || event.ctrlKey) && event.key === "Enter") {
    runQuery();
  }
});

browseButton.addEventListener("click", async () => {
  await loadNodes(questionInput.value.trim());
  switchTab("source");
});

rebuildButton.addEventListener("click", async () => {
  if (!activeRepositoryId) {
    return;
  }

  rebuildButton.disabled = true;
  setStatus("Reindexing repository", "working");
  try {
    await getJson(`/api/index/${encodeURIComponent(activeRepositoryId)}`, { method: "POST" });
    repositoriesCache = await getJson("/api/repos");
    renderRepositories(repositoriesCache);
    await loadSummary();
    await loadNodes(searchInput.value.trim());
    setStatus("Reindex finished", "success");
  } catch (error) {
    setStatus("Reindex failed", "error");
  } finally {
    rebuildButton.disabled = false;
  }
});

for (const tab of document.querySelectorAll(".tab")) {
  tab.addEventListener("click", () => switchTab(tab.dataset.tab));
}

await loadRepositories();
await loadSummary();
await loadNodes();
setStatus("Workspace ready", "success");
