const repositoryList = document.getElementById("repositoryList");
const repositoryForm = document.getElementById("repositoryForm");
const repositoryPathInput = document.getElementById("repositoryPathInput");
const addRepositoryButton = document.getElementById("addRepositoryButton");
const activeRepositoryChip = document.getElementById("activeRepositoryChip");

const queryForm = document.getElementById("queryForm");
const questionInput = document.getElementById("questionInput");
const intentSelect = document.getElementById("intentSelect");
const graphDepthInput = document.getElementById("graphDepthInput");
const limitInput = document.getElementById("limitInput");
const askButton = document.getElementById("askButton");
const browseButton = document.getElementById("browseButton");
const statusBadge = document.getElementById("statusBadge");
const chatTimeline = document.getElementById("chatTimeline");

const searchInput = document.getElementById("searchInput");
const searchButton = document.getElementById("searchButton");
const nodeList = document.getElementById("nodeList");

const sourceMeta = document.getElementById("sourceMeta");
const sourceView = document.getElementById("sourceView");
const contextCards = document.getElementById("contextCards");
const impactCards = document.getElementById("impactCards");

let activeRepositoryId = null;
let workspaceSnapshot = { repositories: [] };
let chatHistory = [];

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

function getActiveRepository() {
  return workspaceSnapshot.repositories.find(item => item.id === activeRepositoryId) ?? null;
}

function formatRelativeTime(value) {
  if (!value) {
    return "未建立索引";
  }

  const then = new Date(value);
  const diffMs = Date.now() - then.getTime();
  const diffMinutes = Math.max(1, Math.round(diffMs / 60000));

  if (diffMinutes < 60) {
    return `${diffMinutes} 分钟前`;
  }

  const diffHours = Math.round(diffMinutes / 60);
  if (diffHours < 48) {
    return `${diffHours} 小时前`;
  }

  const diffDays = Math.round(diffHours / 24);
  return `${diffDays} 天前`;
}

function renderWorkspace(snapshot) {
  workspaceSnapshot = snapshot ?? { repositories: [] };
  const availableIds = new Set(workspaceSnapshot.repositories.map(item => item.id));
  if (!availableIds.has(activeRepositoryId)) {
    activeRepositoryId = workspaceSnapshot.currentRepositoryId ?? workspaceSnapshot.repositories[0]?.id ?? null;
  }

  renderRepositoryCards();
  updateActiveRepositoryChip();
}

function renderRepositoryCards() {
  const repositories = workspaceSnapshot.repositories ?? [];
  if (!repositories.length) {
    repositoryList.innerHTML = `<div class="panelEmpty">暂无仓库，请先添加本地仓库。</div>`;
    return;
  }

  repositoryList.innerHTML = repositories.map(repository => {
    const isActive = repository.id === activeRepositoryId;
    const summary = repository.summary ?? {};
    const branch = repository.branch || "no-git";
    const statusTone = repository.status === "ready" ? "success" : "muted";
    const updated = formatRelativeTime(repository.lastIndexedAt);
    return `
      <article class="repositoryCard${isActive ? " active" : ""}" data-repository-id="${repository.id}">
        <button class="repositorySelect" type="button" data-action="select">
          <div class="repositoryTitleRow">
            <span class="repositoryChevron">${isActive ? "⌄" : "›"}</span>
            <strong>${escapeHtml(repository.displayName)}</strong>
          </div>
          <div class="repositoryPath">${escapeHtml(repository.rootPath)}</div>
          <div class="repositoryMetaLine">
            <span>${escapeHtml(branch)}</span>
            <span>•</span>
            <span>${summary.fileCount ?? 0} files</span>
          </div>
        </button>
        <div class="repositoryFooter">
          <span class="statusPill ${statusTone}">${repository.status === "ready" ? "Ready" : "Not Indexed"}</span>
          <button class="miniAction" type="button" data-action="reindex">Reindex</button>
        </div>
        <div class="repositoryUpdated">Updated ${escapeHtml(updated)}</div>
      </article>
    `;
  }).join("");

  for (const card of repositoryList.querySelectorAll(".repositoryCard")) {
    const repositoryId = card.dataset.repositoryId;
    card.querySelector('[data-action="select"]').addEventListener("click", async () => {
      activeRepositoryId = repositoryId;
      chatHistory = [];
      renderRepositoryCards();
      updateActiveRepositoryChip();
      renderChatTimeline();
      await loadNodes(searchInput.value.trim());
      resetInspector();
    });

    card.querySelector('[data-action="reindex"]').addEventListener("click", async event => {
      event.stopPropagation();
      await reindexRepository(repositoryId);
    });
  }
}

function updateActiveRepositoryChip() {
  const repository = getActiveRepository();
  activeRepositoryChip.textContent = repository?.displayName ?? "未选择";
}

function resetInspector() {
  sourceMeta.textContent = "选择一个符号或证据项查看源码片段。";
  sourceView.textContent = "No source loaded.";
  contextCards.innerHTML = `<div class="panelEmpty">暂无上下文，请先提问或选择符号。</div>`;
  impactCards.innerHTML = `<div class="panelEmpty">暂无影响分析，请先选择符号。</div>`;
}

function renderChatTimeline() {
  if (!chatHistory.length) {
    chatTimeline.innerHTML = `
      <article class="messageCard assistant">
        <div class="messageMeta">系统</div>
        <div class="messageTitle">等待仓库问题</div>
        <p>选择左侧仓库后输入问题，系统会返回上下文摘要、证据、修改建议和源码预览。</p>
      </article>
    `;
    return;
  }

  chatTimeline.innerHTML = chatHistory.map(entry => {
    if (entry.role === "user") {
      return `
        <article class="messageCard user">
          <div class="messageMeta">你</div>
          <div class="messageTitle">${escapeHtml(entry.title)}</div>
          <p>${escapeHtml(entry.body)}</p>
        </article>
      `;
    }

    const result = entry.result;
    const hits = result.hits ?? [];
    const plan = result.suggestedEditPlan;
    const evidenceMarkup = hits.length
      ? `<div class="evidenceCluster">${hits.slice(0, 4).map(hit => `
          <button class="evidencePill" type="button" data-symbol="${encodeURIComponent(hit.symbol ?? "")}" data-file="${encodeURIComponent(hit.filePath ?? "")}">
            <span class="pillTitle">${escapeHtml(hit.title || "Untitled")}</span>
            <span class="pillMeta">${Number(hit.score ?? 0).toFixed(2)}</span>
          </button>
        `).join("")}</div>`
      : `<div class="subtleText">暂无证据命中。</div>`;

    const planMarkup = plan
      ? `
        <div class="planCluster">
          <div class="insightBlock">
            <span class="insightLabel">建议修改目标</span>
            <p>${escapeHtml(plan.goal)}</p>
          </div>
          <div class="insightBlock">
            <span class="insightLabel">关键文件</span>
            <ul>${(plan.filesToInspect || []).slice(0, 4).map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul>
          </div>
          <div class="insightBlock">
            <span class="insightLabel">验证步骤</span>
            <ul>${(plan.verificationSteps || []).slice(0, 4).map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul>
          </div>
        </div>
      `
      : "";

    return `
      <article class="messageCard assistant">
        <div class="messageMeta">CodeBrain</div>
        <div class="messageTitle">${escapeHtml(result.queryText)}</div>
        <p>${escapeHtml(result.context?.summary || "暂无上下文摘要。")}</p>
        <div class="messageStats">
          <span>${escapeHtml(result.retrievalStrategy || "hybrid")}</span>
          <span>${hits.length} evidence</span>
          <span>${escapeHtml(String(result.intent ?? ""))}</span>
        </div>
        ${evidenceMarkup}
        ${planMarkup}
      </article>
    `;
  }).join("");

  for (const pill of chatTimeline.querySelectorAll(".evidencePill")) {
    pill.addEventListener("click", async () => {
      const symbol = decodeURIComponent(pill.dataset.symbol || "");
      const filePath = decodeURIComponent(pill.dataset.file || "");
      await inspectSelection({ symbol, filePath });
    });
  }
}

function renderNodes(nodes) {
  if (!nodes.length) {
    nodeList.innerHTML = `<div class="panelEmpty">暂无符号，请先提问或完成索引。</div>`;
    return;
  }

  nodeList.innerHTML = nodes.map(node => `
    <button class="nodeItem" type="button" data-symbol="${encodeURIComponent(node.symbol ?? "")}" data-file="${encodeURIComponent(node.filePath ?? "")}">
      <strong>${escapeHtml(node.label || "(unnamed)")}</strong>
      <div class="nodeMeta">${escapeHtml(node.kind)}${node.symbol ? ` | ${escapeHtml(node.symbol)}` : ""}</div>
      <div class="nodeMeta">${escapeHtml(node.filePath ?? node.namespace ?? "")}</div>
    </button>
  `).join("");

  for (const element of nodeList.querySelectorAll(".nodeItem")) {
    element.addEventListener("click", async () => {
      const symbol = decodeURIComponent(element.dataset.symbol || "");
      const filePath = decodeURIComponent(element.dataset.file || "");
      await inspectSelection({ symbol, filePath });
    });
  }
}

function renderContextDetails(context) {
  if (!context) {
    contextCards.innerHTML = `<div class="panelEmpty">暂无上下文，请先提问或选择符号。</div>`;
    return;
  }

  const relatedNodes = (context.relatedNodes || []).slice(0, 12);
  const outgoingEdges = (context.outgoingEdges || []).slice(0, 12);
  contextCards.innerHTML = `
    <div class="detailCard">
      <div class="detailHeader">${escapeHtml(context.symbol || "Unknown symbol")}</div>
      <div class="detailSection">
        <span class="insightLabel">Related Nodes</span>
        <ul>${relatedNodes.map(node => `<li>${escapeHtml(node.symbol || node.label)}</li>`).join("")}</ul>
      </div>
      <div class="detailSection">
        <span class="insightLabel">Outgoing Edges</span>
        <ul>${outgoingEdges.map(edge => `<li>${escapeHtml(edge.kind)} -> ${escapeHtml(edge.to)}</li>`).join("")}</ul>
      </div>
    </div>
  `;
}

function renderImpactDetails(impact) {
  if (!impact) {
    impactCards.innerHTML = `<div class="panelEmpty">暂无影响分析，请先选择符号。</div>`;
    return;
  }

  impactCards.innerHTML = `
    <div class="detailCard">
      <div class="detailHeader">${escapeHtml(impact.symbol || "Unknown symbol")}</div>
      <div class="detailSection">
        <span class="insightLabel">Impacted Nodes</span>
        <ul>${(impact.impactedNodes || []).slice(0, 16).map(node => `<li>${escapeHtml(node.symbol || node.label)}</li>`).join("")}</ul>
      </div>
      <div class="detailSection">
        <span class="insightLabel">Traversed Edges</span>
        <ul>${(impact.traversedEdges || []).slice(0, 16).map(edge => `<li>${escapeHtml(edge.kind)}: ${escapeHtml(edge.from)} -> ${escapeHtml(edge.to)}</li>`).join("")}</ul>
      </div>
    </div>
  `;
}

function renderSourceSnippet(snippet) {
  if (!snippet) {
    sourceMeta.textContent = "No source loaded.";
    sourceView.textContent = "No source loaded.";
    return;
  }

  sourceMeta.textContent = `${snippet.filePath} | lines ${snippet.startLine}-${snippet.endLine}`;
  sourceView.textContent = snippet.content;
}

async function loadWorkspace() {
  const snapshot = await getJson("/api/workspace");
  renderWorkspace(snapshot);
}

async function loadNodes(term = "") {
  if (!activeRepositoryId) {
    renderNodes([]);
    return;
  }

  try {
    const nodes = await getJson(`/api/graph/nodes?repositoryId=${encodeURIComponent(activeRepositoryId)}&term=${encodeURIComponent(term)}&limit=50`);
    renderNodes(nodes);
  } catch {
    renderNodes([]);
  }
}

async function runQuery(event) {
  event?.preventDefault();
  if (!activeRepositoryId || !questionInput.value.trim()) {
    return;
  }

  const queryText = questionInput.value.trim();
  chatHistory.unshift({
    role: "user",
    title: getActiveRepository()?.displayName ?? "当前仓库",
    body: queryText
  });
  renderChatTimeline();

  setStatus("Querying repository", "working");
  askButton.disabled = true;
  try {
    const result = await getJson("/api/query", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        query: queryText,
        repositories: [activeRepositoryId],
        intent: intentSelect.value,
        limit: Number(limitInput.value || 8),
        graphDepth: Number(graphDepthInput.value || 2),
      }),
    });

    chatHistory.unshift({
      role: "assistant",
      result
    });
    renderChatTimeline();

    if (result.hits?.length) {
      const lead = result.hits[0];
      await inspectSelection({ symbol: lead.symbol || "", filePath: lead.filePath || "" });
    } else {
      resetInspector();
    }

    await loadNodes(queryText);
    setStatus("Answer ready", "success");
  } catch (error) {
    chatHistory.unshift({
      role: "assistant",
      result: {
        queryText,
        retrievalStrategy: "failed",
        intent: intentSelect.value,
        hits: [],
        context: { summary: `查询失败：${error.message}` },
        suggestedEditPlan: null
      }
    });
    renderChatTimeline();
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

async function addRepository(event) {
  event.preventDefault();
  const path = repositoryPathInput.value.trim();
  if (!path) {
    return;
  }

  addRepositoryButton.disabled = true;
  setStatus("Registering repository", "working");
  try {
    const repository = await getJson("/api/repos", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path })
    });

    repositoryPathInput.value = "";
    await loadWorkspace();
    activeRepositoryId = repository.id;
    chatHistory = [];
    renderRepositoryCards();
    updateActiveRepositoryChip();
    renderChatTimeline();
    await loadNodes();
    resetInspector();
    setStatus("Repository added", "success");
  } catch (error) {
    setStatus(`Add failed`, "error");
  } finally {
    addRepositoryButton.disabled = false;
  }
}

async function reindexRepository(repositoryId) {
  if (!repositoryId) {
    return;
  }

  setStatus("Reindexing repository", "working");
  try {
    await getJson(`/api/index/${encodeURIComponent(repositoryId)}`, { method: "POST" });
    await loadWorkspace();
    if (repositoryId === activeRepositoryId) {
      await loadNodes(searchInput.value.trim());
    }
    setStatus("Reindex finished", "success");
  } catch {
    setStatus("Reindex failed", "error");
  }
}

queryForm.addEventListener("submit", runQuery);
repositoryForm.addEventListener("submit", addRepository);

searchButton.addEventListener("click", () => loadNodes(searchInput.value.trim()));
searchInput.addEventListener("keydown", event => {
  if (event.key === "Enter") {
    loadNodes(searchInput.value.trim());
  }
});

browseButton.addEventListener("click", async () => {
  await loadNodes(questionInput.value.trim());
  switchTab("source");
});

questionInput.addEventListener("keydown", event => {
  if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
    event.preventDefault();
    runQuery();
  }
});

for (const tab of document.querySelectorAll(".tab")) {
  tab.addEventListener("click", () => switchTab(tab.dataset.tab));
}

async function init() {
  try {
    await loadWorkspace();
    await loadNodes();
    resetInspector();
    renderChatTimeline();
    setStatus("Workspace ready", "success");
  } catch {
    setStatus("Workspace unavailable", "error");
  }
}

init();
