#!/usr/bin/env node

const fs = require("fs");
const fsp = require("fs/promises");
const http = require("http");
const os = require("os");
const path = require("path");
const { spawn, spawnSync } = require("child_process");
const { randomUUID } = require("crypto");

const HOST = process.env.LWM_HOST || "127.0.0.1";
const PORT = Number(process.env.LWM_PORT || 4177);
const APP_SUPPORT = path.join(os.homedir(), "Library", "Application Support", "LANWebTerminalManager");
const CONFIG_PATH = path.join(APP_SUPPORT, "endpoints.json");
const PUBLIC_DIR = path.join(__dirname, "public");

function endpointFiles(endpoint) {
  return {
    pidFile: path.join(endpoint.rootPath, `.lan-web-terminal-${endpoint.port}.pid`),
    logFile: path.join(endpoint.rootPath, `.lan-web-terminal-${endpoint.port}.log`)
  };
}

function run(command, args = [], options = {}) {
  const result = spawnSync(command, args, {
    cwd: options.cwd,
    encoding: "utf8",
    timeout: options.timeout || 10000,
    shell: false
  });
  return {
    ok: result.status === 0,
    status: result.status ?? -1,
    stdout: result.stdout || "",
    stderr: result.stderr || result.error?.message || ""
  };
}

async function ensureSupportDir() {
  await fsp.mkdir(APP_SUPPORT, { recursive: true });
}

async function loadEndpoints() {
  await ensureSupportDir();
  try {
    const data = await fsp.readFile(CONFIG_PATH, "utf8");
    const endpoints = JSON.parse(data);
    return Array.isArray(endpoints) ? endpoints : [];
  } catch {
    const seeded = seedDefaultEndpoints();
    await saveEndpoints(seeded);
    return seeded;
  }
}

async function saveEndpoints(endpoints) {
  await ensureSupportDir();
  await fsp.writeFile(CONFIG_PATH, JSON.stringify(endpoints, null, 2), "utf8");
}

function seedDefaultEndpoints() {
  const candidates = [
    { name: "DestinyApp", rootPath: "/Users/ryan/WorkSpace/MyProject/DestinyApp", port: 8089, host: "0.0.0.0", urlPath: "/web/", autoOpen: false },
    { name: "战斗设计", rootPath: "/Users/ryan/Perforce/Tools&Docs/DesignDocs/战斗设计", port: 8088, host: "0.0.0.0", urlPath: "/", autoOpen: false }
  ];
  return candidates.filter((item) => fs.existsSync(item.rootPath)).map((item) => ({ id: randomUUID(), ...item }));
}

function normalizeURLPath(value) {
  let next = String(value || "").trim();
  if (!next) next = "/";
  if (!next.startsWith("/")) next = `/${next}`;
  return next;
}

function listenerPids(port) {
  const result = run("/usr/sbin/lsof", ["-nP", `-iTCP:${port}`, "-sTCP:LISTEN", "-t"]);
  return result.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => /^\d+$/.test(line))
    .map((line) => Number(line))
    .filter(Number.isFinite)
    .sort((a, b) => a - b);
}

function pidFromFile(file) {
  try {
    const value = fs.readFileSync(file, "utf8").trim();
    const pid = Number(value);
    return Number.isFinite(pid) ? pid : null;
  } catch {
    return null;
  }
}

function isPortOpen(host, port) {
  const target = host === "0.0.0.0" ? "127.0.0.1" : host;
  return run("/usr/bin/nc", ["-z", target, String(port)], { timeout: 2500 }).ok;
}

function primaryLANIP() {
  const route = run("/sbin/route", ["-n", "get", "default"]);
  const line = route.stdout.split(/\r?\n/).find((item) => item.trim().startsWith("interface:"));
  const iface = line?.split(":").slice(1).join(":").trim();
  if (iface) {
    const ip = run("/usr/sbin/ipconfig", ["getifaddr", iface]).stdout.trim();
    if (isPrivateIPv4(ip)) return ip;
  }
  return null;
}

function lanIPs() {
  const primary = primaryLANIP();
  if (primary) return [primary];
  const ifconfig = run("/sbin/ifconfig").stdout;
  const matches = ifconfig.match(/\binet (192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(?:1[6-9]|2\d|3[0-1])\.\d+\.\d+)\b/g) || [];
  return [...new Set(matches.map((item) => item.replace(/^inet /, "")))];
}

function isPrivateIPv4(value) {
  return /^(192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[0-1])\.\d+\.\d+)$/.test(value);
}

function pageCount(rootPath) {
  let count = 0;
  function walk(dir) {
    let entries = [];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      const rel = path.relative(rootPath, full).toLowerCase();
      if (entry.isDirectory()) {
        if (![".git", "node_modules", ".build", "build"].includes(entry.name)) walk(full);
      } else if ((rel.endsWith(".html") || rel.endsWith(".md")) && rel !== "index.html" && !rel.endsWith("/index.html")) {
        count += 1;
      }
    }
  }
  walk(rootPath);
  return count;
}

function mtime(file) {
  try {
    return new Date(fs.statSync(file).mtime).toISOString().replace("T", " ").slice(0, 19);
  } catch {
    return "--";
  }
}

function tail(file, maxLines = 80) {
  try {
    return fs.readFileSync(file, "utf8").split(/\r?\n/).slice(-maxLines).join("\n") || "暂无日志";
  } catch {
    return "暂无日志";
  }
}

function statusFor(endpoint) {
  const files = endpointFiles(endpoint);
  const pids = listenerPids(endpoint.port);
  const urlPath = normalizeURLPath(endpoint.urlPath);
  return {
    running: pids.length > 0 || isPortOpen(endpoint.host, endpoint.port),
    pids,
    urls: lanIPs().map((ip) => `http://${ip}:${endpoint.port}${urlPath}`),
    localURL: `http://127.0.0.1:${endpoint.port}${urlPath}`,
    pageCount: pageCount(endpoint.rootPath),
    indexMtime: mtime(path.join(endpoint.rootPath, "index.html")),
    logTail: tail(files.logFile),
    updatedAt: new Date().toISOString().replace("T", " ").slice(0, 19)
  };
}

async function enrichEndpoints(endpoints) {
  return endpoints.map((endpoint) => ({ ...endpoint, status: statusFor(endpoint) }));
}

async function readJSON(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? JSON.parse(text) : {};
}

function send(response, status, payload, headers = {}) {
  const body = typeof payload === "string" ? payload : JSON.stringify(payload);
  response.writeHead(status, {
    "Content-Type": typeof payload === "string" ? "text/plain; charset=utf-8" : "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    ...headers
  });
  response.end(body);
}

function sendError(response, status, message) {
  send(response, status, { error: message });
}

async function findEndpoint(id) {
  const endpoints = await loadEndpoints();
  const index = endpoints.findIndex((endpoint) => endpoint.id === id);
  return { endpoints, endpoint: endpoints[index], index };
}

async function startEndpoint(endpoint) {
  if (!fs.existsSync(endpoint.rootPath)) throw new Error(`目录不存在：${endpoint.rootPath}`);
  if (statusFor(endpoint).running) return;
  const files = endpointFiles(endpoint);
  fs.closeSync(fs.openSync(files.logFile, "a"));
  const out = fs.openSync(files.logFile, "a");
  const child = spawn("/usr/bin/python3", ["-m", "http.server", String(endpoint.port), "--bind", endpoint.host], {
    cwd: endpoint.rootPath,
    detached: true,
    stdio: ["ignore", out, out]
  });
  child.unref();
  fs.writeFileSync(files.pidFile, String(child.pid), "utf8");
}

async function stopEndpoint(endpoint) {
  const files = endpointFiles(endpoint);
  const pids = new Set(listenerPids(endpoint.port));
  const filePid = pidFromFile(files.pidFile);
  if (filePid) pids.add(filePid);
  for (const pid of [...pids].sort((a, b) => a - b)) {
    try {
      process.kill(pid, "SIGTERM");
    } catch {}
  }
  try {
    fs.unlinkSync(files.pidFile);
  } catch {}
}

function listHomepages(rootPath) {
  const items = [];
  function walk(dir, depth) {
    if (depth > 5 || items.length >= 300) return;
    let entries = [];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      if (entry.name.startsWith(".") || entry.name === "node_modules") continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full, depth + 1);
      } else if (/\.(html?|md)$/i.test(entry.name)) {
        items.push(`/${path.relative(rootPath, full).split(path.sep).join("/")}`);
      }
    }
  }
  walk(rootPath, 0);
  return items.sort((a, b) => {
    const ai = a.toLowerCase().includes("index") ? 0 : 1;
    const bi = b.toLowerCase().includes("index") ? 0 : 1;
    return ai - bi || a.localeCompare(b);
  });
}

async function handleAPI(request, response, url) {
  const parts = url.pathname.split("/").filter(Boolean);
  const endpoints = await loadEndpoints();

  if (request.method === "GET" && url.pathname === "/api/endpoints") {
    send(response, 200, { endpoints: await enrichEndpoints(endpoints) });
    return;
  }

  if (request.method === "POST" && url.pathname === "/api/endpoints") {
    const body = await readJSON(request);
    const rootPath = String(body.rootPath || "").trim();
    if (!rootPath) return sendError(response, 400, "请输入网页目录路径");
    if (!fs.existsSync(rootPath)) return sendError(response, 400, `目录不存在：${rootPath}`);
    const usedPorts = new Set(endpoints.map((endpoint) => Number(endpoint.port)));
    let port = Number(body.port || 8088);
    while (usedPorts.has(port)) port += 1;
    const endpoint = {
      id: randomUUID(),
      name: String(body.name || path.basename(rootPath) || "网页终端"),
      rootPath,
      port,
      host: body.host === "127.0.0.1" ? "127.0.0.1" : "0.0.0.0",
      urlPath: fs.existsSync(path.join(rootPath, "web")) ? "/web/" : "/",
      autoOpen: false
    };
    endpoints.push(endpoint);
    await saveEndpoints(endpoints);
    send(response, 200, { endpoint: { ...endpoint, status: statusFor(endpoint) } });
    return;
  }

  if (parts[0] === "api" && parts[1] === "endpoints" && parts[2]) {
    const { endpoints: current, endpoint, index } = await findEndpoint(parts[2]);
    if (!endpoint) return sendError(response, 404, "未找到网页终端");

    if (request.method === "PATCH" && parts.length === 3) {
      const body = await readJSON(request);
      current[index] = {
        ...endpoint,
        name: String(body.name ?? endpoint.name),
        rootPath: String(body.rootPath ?? endpoint.rootPath),
        port: Number(body.port ?? endpoint.port),
        host: body.host === "127.0.0.1" ? "127.0.0.1" : "0.0.0.0",
        urlPath: normalizeURLPath(body.urlPath ?? endpoint.urlPath),
        autoOpen: Boolean(body.autoOpen ?? endpoint.autoOpen)
      };
      await saveEndpoints(current);
      send(response, 200, { endpoint: { ...current[index], status: statusFor(current[index]) } });
      return;
    }

    if (request.method === "DELETE" && parts.length === 3) {
      await stopEndpoint(endpoint);
      current.splice(index, 1);
      await saveEndpoints(current);
      send(response, 200, { ok: true });
      return;
    }

    if (request.method === "POST" && parts[3] === "start") {
      await startEndpoint(endpoint);
      send(response, 200, { endpoint: { ...endpoint, status: statusFor(endpoint) } });
      return;
    }

    if (request.method === "POST" && parts[3] === "stop") {
      await stopEndpoint(endpoint);
      send(response, 200, { endpoint: { ...endpoint, status: statusFor(endpoint) } });
      return;
    }

    if (request.method === "POST" && parts[3] === "command") {
      const body = await readJSON(request);
      const command = String(body.command || "").trim();
      if (!command) return sendError(response, 400, "请输入命令");
      const result = run("/bin/zsh", ["-lc", command], { cwd: endpoint.rootPath, timeout: 60000 });
      send(response, 200, { ...result, output: [result.stdout, result.stderr].filter(Boolean).join("\n") || "(无输出)" });
      return;
    }

    if (request.method === "GET" && parts[3] === "homepages") {
      send(response, 200, { items: listHomepages(endpoint.rootPath) });
      return;
    }
  }

  sendError(response, 404, "未知 API");
}

async function serveStatic(response, url) {
  if (url.pathname === "/favicon.ico") {
    response.writeHead(204, { "Cache-Control": "public, max-age=86400" });
    response.end();
    return;
  }

  const pathname = url.pathname === "/" ? "/index.html" : decodeURIComponent(url.pathname);
  const filePath = path.normalize(path.join(PUBLIC_DIR, pathname));
  if (!filePath.startsWith(PUBLIC_DIR)) return sendError(response, 403, "Forbidden");
  try {
    const data = await fsp.readFile(filePath);
    const ext = path.extname(filePath);
    const type = ext === ".html" ? "text/html" : ext === ".css" ? "text/css" : ext === ".js" ? "text/javascript" : "application/octet-stream";
    response.writeHead(200, { "Content-Type": `${type}; charset=utf-8` });
    response.end(data);
  } catch {
    sendError(response, 404, "Not found");
  }
}

const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, `http://${request.headers.host || `${HOST}:${PORT}`}`);
    if (url.pathname.startsWith("/api/")) {
      await handleAPI(request, response, url);
    } else {
      await serveStatic(response, url);
    }
  } catch (error) {
    sendError(response, 500, error.message || "服务器错误");
  }
});

server.listen(PORT, HOST, () => {
  console.log(`LAN Web Terminal Manager web version: http://${HOST}:${PORT}`);
});
