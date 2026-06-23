const state = {
  endpoints: [],
  selectedId: null,
  terminalOutput: "选择一个网页目录后，可以在这里执行维护命令。",
  savingTimer: null
};

const $ = (id) => document.getElementById(id);

function selectedEndpoint() {
  return state.endpoints.find((endpoint) => endpoint.id === state.selectedId) || state.endpoints[0] || null;
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options,
    body: options.body ? JSON.stringify(options.body) : undefined
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "请求失败");
  return data;
}

function setActivity(text) {
  $("activity").textContent = text;
}

async function loadEndpoints(keepSelection = true) {
  const data = await api("/api/endpoints");
  state.endpoints = data.endpoints;
  if (!keepSelection || !state.endpoints.some((endpoint) => endpoint.id === state.selectedId)) {
    state.selectedId = state.endpoints[0]?.id || null;
  }
  render();
}

function render() {
  renderSidebar();
  renderDetail();
}

function renderSidebar() {
  const list = $("endpointList");
  list.innerHTML = "";
  for (const endpoint of state.endpoints) {
    const button = document.createElement("button");
    button.className = `endpoint ${endpoint.id === state.selectedId ? "selected" : ""}`;
    button.innerHTML = `
      <span class="dot ${endpoint.status?.running ? "running" : ""}"></span>
      <span>
        <strong></strong>
        <span></span>
      </span>
    `;
    button.querySelector("strong").textContent = endpoint.name;
    button.querySelector("span span").textContent = `${endpoint.host}:${endpoint.port}`;
    button.addEventListener("click", () => {
      state.selectedId = endpoint.id;
      render();
      loadHomepages(endpoint.id);
    });
    list.appendChild(button);
  }
  $("deleteEndpoint").disabled = !selectedEndpoint();
}

function renderDetail() {
  const endpoint = selectedEndpoint();
  $("emptyState").classList.toggle("hidden", Boolean(endpoint));
  $("detail").classList.toggle("hidden", !endpoint);
  if (!endpoint) return;

  const status = endpoint.status || {};
  $("title").textContent = endpoint.name;
  $("rootPath").textContent = endpoint.rootPath;
  $("nameInput").value = endpoint.name;
  $("pathInput").value = endpoint.rootPath;
  $("hostInput").value = endpoint.host;
  $("portInput").value = endpoint.port;
  $("urlPathInput").value = endpoint.urlPath;
  $("autoOpenInput").checked = endpoint.autoOpen;

  $("statusValue").textContent = status.running ? "运行中" : "已停止";
  $("pageCount").textContent = status.pageCount ?? "--";
  $("pidValue").textContent = status.pids?.length ? status.pids.join(", ") : "--";
  $("indexMtime").textContent = status.indexMtime || "--";
  $("logOutput").textContent = status.logTail || "暂无日志";
  $("terminalOutput").textContent = state.terminalOutput;

  $("startButton").disabled = status.running;
  $("stopButton").disabled = !status.running;
  $("openLANButton").disabled = !status.running || !status.urls?.length;
  $("copyURLsButton").disabled = !status.running || !status.urls?.length;
  $("openLocalButton").disabled = !status.running;

  const urlList = $("urlList");
  urlList.innerHTML = "";
  if (status.running && status.urls?.length) {
    for (const url of status.urls) {
      const item = document.createElement("div");
      item.className = "url-item";
      item.textContent = url;
      urlList.appendChild(item);
    }
  } else {
    const item = document.createElement("div");
    item.className = "url-item";
    item.textContent = status.running ? "没有检测到可用的局域网地址。" : "服务停止后地址不可访问，启动后可复制或打开。";
    urlList.appendChild(item);
  }
}

function updateSelected(mutator) {
  const endpoint = selectedEndpoint();
  if (!endpoint) return;
  mutator(endpoint);
  render();
}

function scheduleSave() {
  clearTimeout(state.savingTimer);
  state.savingTimer = setTimeout(saveSelected, 350);
}

async function saveSelected() {
  const endpoint = selectedEndpoint();
  if (!endpoint) return;
  try {
    const body = {
      name: $("nameInput").value,
      rootPath: $("pathInput").value,
      host: $("hostInput").value,
      port: Number($("portInput").value),
      urlPath: $("urlPathInput").value,
      autoOpen: $("autoOpenInput").checked
    };
    const data = await api(`/api/endpoints/${endpoint.id}`, { method: "PATCH", body });
    const index = state.endpoints.findIndex((item) => item.id === endpoint.id);
    state.endpoints[index] = data.endpoint;
    setActivity("已保存配置");
    render();
  } catch (error) {
    setActivity(error.message);
  }
}

async function loadHomepages(id) {
  const endpoint = state.endpoints.find((item) => item.id === id);
  const select = $("homepageSelect");
  select.innerHTML = "";
  if (!endpoint) return;
  try {
    const data = await api(`/api/endpoints/${id}/homepages`);
    const current = document.createElement("option");
    current.value = endpoint.urlPath;
    current.textContent = endpoint.urlPath;
    select.appendChild(current);
    for (const item of data.items) {
      if (item === endpoint.urlPath) continue;
      const option = document.createElement("option");
      option.value = item;
      option.textContent = item;
      select.appendChild(option);
    }
  } catch {
    const option = document.createElement("option");
    option.value = endpoint.urlPath;
    option.textContent = endpoint.urlPath;
    select.appendChild(option);
  }
}

function bindInputs() {
  $("nameInput").addEventListener("input", () => {
    updateSelected((endpoint) => { endpoint.name = $("nameInput").value; });
    scheduleSave();
  });
  $("pathInput").addEventListener("input", () => updateSelected((endpoint) => { endpoint.rootPath = $("pathInput").value; }));
  $("savePathButton").addEventListener("click", saveSelected);
  $("hostInput").addEventListener("change", saveSelected);
  $("portInput").addEventListener("change", saveSelected);
  $("urlPathInput").addEventListener("input", () => updateSelected((endpoint) => { endpoint.urlPath = $("urlPathInput").value; }));
  $("saveHomepageButton").addEventListener("click", saveSelected);
  $("autoOpenInput").addEventListener("change", saveSelected);
  $("homepageSelect").addEventListener("change", () => {
    $("urlPathInput").value = $("homepageSelect").value;
    saveSelected();
  });
}

function bindActions() {
  $("addEndpoint").addEventListener("click", () => $("addDialog").showModal());
  $("confirmAdd").addEventListener("click", async (event) => {
    event.preventDefault();
    try {
      const body = { rootPath: $("newRootPath").value, name: $("newName").value };
      const data = await api("/api/endpoints", { method: "POST", body });
      state.endpoints.push(data.endpoint);
      state.selectedId = data.endpoint.id;
      $("newRootPath").value = "";
      $("newName").value = "";
      $("addDialog").close();
      setActivity(`已添加：${data.endpoint.name}`);
      render();
      loadHomepages(data.endpoint.id);
    } catch (error) {
      setActivity(error.message);
    }
  });

  $("deleteEndpoint").addEventListener("click", async () => {
    const endpoint = selectedEndpoint();
    if (!endpoint) return;
    if (!confirm(`删除 ${endpoint.name}？`)) return;
    try {
      await api(`/api/endpoints/${endpoint.id}`, { method: "DELETE" });
      setActivity(`已删除：${endpoint.name}`);
      await loadEndpoints(false);
    } catch (error) {
      setActivity(error.message);
    }
  });

  $("startButton").addEventListener("click", () => endpointAction("start"));
  $("stopButton").addEventListener("click", () => endpointAction("stop"));
  $("openLANButton").addEventListener("click", () => {
    const url = selectedEndpoint()?.status?.urls?.[0];
    if (url) window.open(url, "_blank", "noopener");
  });
  $("openLocalButton").addEventListener("click", () => {
    const url = selectedEndpoint()?.status?.localURL;
    if (url) window.open(url, "_blank", "noopener");
  });
  $("copyURLsButton").addEventListener("click", async () => {
    const urls = selectedEndpoint()?.status?.urls || [];
    if (!urls.length) return;
    await navigator.clipboard.writeText(urls.join("\n"));
    setActivity("已复制访问地址");
  });
  $("runCommandButton").addEventListener("click", runCommand);
  $("commandInput").addEventListener("keydown", (event) => {
    if (event.key === "Enter") runCommand();
  });
}

async function endpointAction(action) {
  const endpoint = selectedEndpoint();
  if (!endpoint) return;
  try {
    const data = await api(`/api/endpoints/${endpoint.id}/${action}`, { method: "POST" });
    const index = state.endpoints.findIndex((item) => item.id === endpoint.id);
    state.endpoints[index] = data.endpoint;
    setActivity(action === "start" ? `已启动：${endpoint.name}` : `已停止：${endpoint.name}`);
    render();
    if (action === "start" && data.endpoint.autoOpen && data.endpoint.status?.urls?.[0]) {
      window.open(data.endpoint.status.urls[0], "_blank", "noopener");
    }
  } catch (error) {
    setActivity(error.message);
  }
}

async function runCommand() {
  const endpoint = selectedEndpoint();
  const command = $("commandInput").value.trim();
  if (!endpoint || !command) return;
  $("commandInput").value = "";
  state.terminalOutput += `\n\n$ ${command}`;
  render();
  try {
    const result = await api(`/api/endpoints/${endpoint.id}/command`, { method: "POST", body: { command } });
    state.terminalOutput += `\n${result.output}`;
    setActivity(result.ok ? "命令完成" : `命令失败，退出码 ${result.status}`);
    await loadEndpoints();
  } catch (error) {
    state.terminalOutput += `\n${error.message}`;
    setActivity("命令失败");
    render();
  }
}

bindInputs();
bindActions();
loadEndpoints(false).then(() => {
  const endpoint = selectedEndpoint();
  if (endpoint) loadHomepages(endpoint.id);
});
setInterval(() => loadEndpoints().catch((error) => setActivity(error.message)), 3000);
