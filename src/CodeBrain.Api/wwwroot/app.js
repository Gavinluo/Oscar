const repositorySelect = document.getElementById("repositorySelect");
const summaryGrid = document.getElementById("summaryGrid");
const nodeList = document.getElementById("nodeList");
const contextView = document.getElementById("contextView");
const impactView = document.getElementById("impactView");
const searchInput = document.getElementById("searchInput");
const searchButton = document.getElementById("searchButton");
const rebuildButton = document.getElementById("rebuildButton");

let activeRepositoryId = null;

async function getJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return await response.json();
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

function renderNodes(nodes) {
  nodeList.innerHTML = nodes.map(node => `
    <button class="listItem" data-symbol="${encodeURIComponent(node.symbol ?? "")}">
      <strong>${node.label}</strong>
      <div class="meta">${node.kind}${node.symbol ? ` | ${node.symbol}` : ""}</div>
      <div class="meta">${node.filePath ?? node.namespace ?? ""}</div>
    </button>
  `).join("");

  for (const element of nodeList.querySelectorAll(".listItem")) {
    element.addEventListener("click", async () => {
      const symbol = decodeURIComponent(element.dataset.symbol);
      if (!symbol || !activeRepositoryId) {
        return;
      }

      await loadContext(symbol);
      await loadImpact(symbol);
    });
  }
}

function renderRepositories(repositories) {
  repositorySelect.innerHTML = repositories.map(repo => `
    <option value="${repo.id}">${repo.displayName} (${repo.id})</option>
  `).join("");

  activeRepositoryId = repositories[0]?.id ?? null;
  repositorySelect.value = activeRepositoryId ?? "";
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
}

async function loadNodes(term = "") {
  if (!activeRepositoryId) {
    renderNodes([]);
    return;
  }

  const nodes = await getJson(`/api/graph/nodes?repositoryId=${encodeURIComponent(activeRepositoryId)}&term=${encodeURIComponent(term)}&limit=50`);
  renderNodes(nodes);
}

async function loadContext(symbol) {
  const context = await getJson(`/api/graph/context?repositoryId=${encodeURIComponent(activeRepositoryId)}&symbol=${encodeURIComponent(symbol)}`);
  contextView.textContent = JSON.stringify(context, null, 2);
}

async function loadImpact(symbol) {
  const impact = await getJson(`/api/graph/impact?repositoryId=${encodeURIComponent(activeRepositoryId)}&symbol=${encodeURIComponent(symbol)}&depth=2&limit=30`);
  impactView.textContent = JSON.stringify(impact, null, 2);
}

repositorySelect.addEventListener("change", async () => {
  activeRepositoryId = repositorySelect.value || null;
  contextView.textContent = "Select a symbol from the left panel.";
  impactView.textContent = "Select a symbol from the left panel.";
  await loadSummary();
  await loadNodes(searchInput.value.trim());
});

searchButton.addEventListener("click", () => loadNodes(searchInput.value.trim()));
searchInput.addEventListener("keydown", event => {
  if (event.key === "Enter") {
    loadNodes(searchInput.value.trim());
  }
});

rebuildButton.addEventListener("click", async () => {
  if (!activeRepositoryId) {
    return;
  }

  rebuildButton.disabled = true;
  rebuildButton.textContent = "Reindexing...";
  try {
    await getJson(`/api/index/${encodeURIComponent(activeRepositoryId)}`, { method: "POST" });
    await loadSummary();
    await loadNodes(searchInput.value.trim());
  } finally {
    rebuildButton.disabled = false;
    rebuildButton.textContent = "Reindex";
  }
});

await loadRepositories();
await loadSummary();
await loadNodes();
