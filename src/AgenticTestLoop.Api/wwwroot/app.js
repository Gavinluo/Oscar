const summaryGrid = document.getElementById("summaryGrid");
const nodeList = document.getElementById("nodeList");
const contextView = document.getElementById("contextView");
const impactView = document.getElementById("impactView");
const searchInput = document.getElementById("searchInput");
const searchButton = document.getElementById("searchButton");
const rebuildButton = document.getElementById("rebuildButton");

async function getJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return await response.json();
}

function renderSummary(summary) {
  const items = [
    ["节点数", summary.nodeCount ?? 0],
    ["边数", summary.edgeCount ?? 0],
    ["项目数", summary.projectCount ?? 0],
    ["符号数", summary.symbolCount ?? 0],
    ["理解卡片", summary.understandingCardCount ?? 0],
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
      if (!symbol) return;
      await loadContext(symbol);
      await loadImpact(symbol);
    });
  }
}

async function loadSummary() {
  renderSummary(await getJson("/api/graph/summary"));
}

async function loadNodes(term = "") {
  const nodes = await getJson(`/api/graph/nodes?term=${encodeURIComponent(term)}&limit=50`);
  renderNodes(nodes);
}

async function loadContext(symbol) {
  const context = await getJson(`/api/graph/context?symbol=${encodeURIComponent(symbol)}`);
  contextView.textContent = JSON.stringify(context, null, 2);
}

async function loadImpact(symbol) {
  const impact = await getJson(`/api/graph/impact?symbol=${encodeURIComponent(symbol)}&depth=2&limit=30`);
  impactView.textContent = JSON.stringify(impact, null, 2);
}

searchButton.addEventListener("click", () => loadNodes(searchInput.value.trim()));
searchInput.addEventListener("keydown", event => {
  if (event.key === "Enter") {
    loadNodes(searchInput.value.trim());
  }
});

rebuildButton.addEventListener("click", async () => {
  rebuildButton.disabled = true;
  rebuildButton.textContent = "重建中...";
  try {
    await getJson("/api/graph/rebuild", { method: "POST" });
    await loadSummary();
    await loadNodes(searchInput.value.trim());
  } finally {
    rebuildButton.disabled = false;
    rebuildButton.textContent = "重建图谱";
  }
});

await loadSummary();
await loadNodes();
