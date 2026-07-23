import http from "node:http";
import net from "node:net";
import { URL } from "node:url";

const PIPE_NAME = process.env.GENESIS_UI_PIPE_NAME || "\\\\.\\pipe\\city-genesis/tauri-ipc";
const HTTP_PORT = Number(process.env.GENESIS_UI_MOCK_PARENT_PORT || 18765);

let tauriSocket = null;
let tauriBuffer = "";
const clients = new Set();
const history = [];
let shuttingDown = false;

process.on("uncaughtException", (error) => {
  console.error(error);
  process.exit(1);
});

function now() {
  return new Date().toISOString();
}

function pushEvent(type, payload) {
  const entry = { type, payload, timestamp: now() };
  history.push(entry);
  if (history.length > 200) {
    history.shift();
  }
  const serialized = `data: ${JSON.stringify(entry)}\n\n`;
  for (const client of clients) {
    client.write(serialized);
  }
}

function makeEnvelope(event, payload) {
  return {
    version: "1.0",
    request_id: `mock-${Date.now()}-${Math.random().toString(16).slice(2, 8)}`,
    timestamp: now(),
    source: "parent",
    destination: "tauri",
    event,
    payload,
  };
}

function sendEnvelope(envelope) {
  if (!tauriSocket) {
    throw new Error("Tauri is not connected yet.");
  }
  tauriSocket.write(`${JSON.stringify(envelope)}\n`);
  pushEvent("sent", envelope);
}

function closeSseClients() {
  for (const client of clients) {
    client.end();
  }
  clients.clear();
}

function closeServer(server) {
  return new Promise((resolve) => {
    server.close(() => resolve());
  });
}

async function shutdownMockParent(exitCode = 0) {
  if (shuttingDown) {
    return;
  }

  shuttingDown = true;
  pushEvent("status", { shuttingDown: true });
  closeSseClients();

  if (tauriSocket) {
    tauriSocket.destroy();
    tauriSocket = null;
  }

  await Promise.allSettled([closeServer(httpServer), closeServer(pipeServer)]);
  process.exit(exitCode);
}

const pipeServer = net.createServer((socket) => {
  tauriSocket = socket;
  tauriBuffer = "";
  pushEvent("status", { connected: true, pipe: PIPE_NAME });

  socket.on("data", (chunk) => {
    tauriBuffer += chunk.toString("utf8");
    const lines = tauriBuffer.split(/\r?\n/);
    tauriBuffer = lines.pop() ?? "";
    for (const line of lines) {
      if (!line.trim()) {
        continue;
      }
      try {
        pushEvent("received", JSON.parse(line));
      } catch (error) {
        pushEvent("parse-error", { line, message: String(error) });
      }
    }
  });

  socket.on("close", () => {
    tauriSocket = null;
    pushEvent("status", { connected: false, pipe: PIPE_NAME });
  });

  socket.on("error", (error) => {
    pushEvent("socket-error", { message: String(error) });
  });
});

pipeServer.on("error", async (error) => {
  if (error?.code === "EADDRINUSE") {
    const existingUi = await probeExistingMockParent();
    if (existingUi) {
      console.log(`Mock parent is already running.`);
      console.log(`Open: ${existingUi}`);
      process.exit(0);
    }

    console.error(`Named pipe is already in use: ${PIPE_NAME}`);
    console.error(`Run 'npm run cleanup-dev' and try again.`);
    process.exit(1);
  }

  console.error(error);
  process.exit(1);
});

pipeServer.listen(PIPE_NAME, () => {
  console.log(`Mock parent named pipe server: ${PIPE_NAME}`);
  pushEvent("status", { connected: false, pipe: PIPE_NAME });
});

const httpServer = http.createServer(async (req, res) => {
  const url = new URL(req.url || "/", `http://${req.headers.host || "127.0.0.1"}`);

  if (req.method === "GET" && url.pathname === "/") {
    res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    res.end(renderHtml());
    return;
  }

  if (req.method === "GET" && url.pathname === "/events") {
    res.writeHead(200, {
      "content-type": "text/event-stream",
      "cache-control": "no-cache",
      connection: "keep-alive",
    });
    clients.add(res);
    for (const entry of history) {
      res.write(`data: ${JSON.stringify(entry)}\n\n`);
    }
    req.on("close", () => {
      clients.delete(res);
    });
    return;
  }

  if (req.method === "GET" && url.pathname === "/api/state") {
    res.writeHead(200, { "content-type": "application/json; charset=utf-8" });
    res.end(
      JSON.stringify(
        {
          connected: Boolean(tauriSocket),
          pipeName: PIPE_NAME,
          history,
        },
        null,
        2,
      ),
    );
    return;
  }

  if (req.method === "POST" && url.pathname === "/api/send") {
    const body = await readBody(req);
    try {
      const input = JSON.parse(body);
      const envelope = makeEnvelope(input.event, input.payload);
      sendEnvelope(envelope);
      res.writeHead(200, { "content-type": "application/json; charset=utf-8" });
      res.end(JSON.stringify({ ok: true }));
    } catch (error) {
      res.writeHead(400, { "content-type": "application/json; charset=utf-8" });
      res.end(JSON.stringify({ ok: false, message: String(error) }));
    }
    return;
  }

  if (req.method === "POST" && url.pathname === "/api/exit") {
    res.writeHead(200, { "content-type": "application/json; charset=utf-8" });
    res.end(JSON.stringify({ ok: true }));
    setTimeout(() => {
      shutdownMockParent(0).catch((error) => {
        console.error(error);
        process.exit(1);
      });
    }, 10);
    return;
  }

  res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
  res.end("Not Found");
});

httpServer.on("error", (error) => {
  if (error?.code === "EADDRINUSE") {
    console.log(`Mock parent UI is already running.`);
    console.log(`Open: http://127.0.0.1:${HTTP_PORT}`);
    process.exit(0);
  }

  console.error(error);
  process.exit(1);
});

httpServer.listen(HTTP_PORT, "127.0.0.1", () => {
  console.log(`Mock parent UI: http://127.0.0.1:${HTTP_PORT}`);
});

process.on("SIGINT", () => {
  shutdownMockParent(0).catch((error) => {
    console.error(error);
    process.exit(1);
  });
});

process.on("SIGTERM", () => {
  shutdownMockParent(0).catch((error) => {
    console.error(error);
    process.exit(1);
  });
});

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.on("data", (chunk) => {
      body += chunk.toString("utf8");
    });
    req.on("end", () => resolve(body));
    req.on("error", reject);
  });
}

function probeExistingMockParent() {
  return new Promise((resolve) => {
    const request = http.get(`http://127.0.0.1:${HTTP_PORT}/api/state`, (response) => {
      if (response.statusCode === 200) {
        response.resume();
        resolve(`http://127.0.0.1:${HTTP_PORT}`);
        return;
      }

      response.resume();
      resolve(null);
    });

    request.on("error", () => resolve(null));
    request.setTimeout(1200, () => {
      request.destroy();
      resolve(null);
    });
  });
}

function renderHtml() {
  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Genesis UI Mock Parent</title>
    <style>
      :root {
        color-scheme: dark;
        font-family: "Segoe UI", sans-serif;
        background: #0d1320;
        color: #e8f1fb;
      }
      body {
        margin: 0;
        padding: 24px;
        background: #0d1320;
      }
      .layout {
        display: grid;
        grid-template-columns: 420px 1fr;
        gap: 18px;
      }
      .panel {
        background: rgba(255, 255, 255, 0.04);
        border: 1px solid rgba(255, 255, 255, 0.08);
        border-radius: 8px;
        padding: 16px;
      }
      label {
        display: grid;
        gap: 6px;
        margin-bottom: 12px;
        font-size: 14px;
      }
      input, select, textarea, button {
        font: inherit;
        border-radius: 10px;
        border: 1px solid rgba(255, 255, 255, 0.12);
        background: rgba(255, 255, 255, 0.05);
        color: inherit;
        padding: 10px 12px;
      }
      textarea {
        min-height: 160px;
        resize: vertical;
      }
      button {
        cursor: pointer;
      }
      .row {
        display: flex;
        gap: 10px;
      }
      pre {
        margin: 0;
        overflow: auto;
        white-space: pre-wrap;
        word-break: break-word;
        font-size: 12px;
      }
      .log {
        display: grid;
        gap: 10px;
        max-height: 80vh;
        overflow: auto;
      }
      .entry {
        padding: 12px;
        border-radius: 12px;
        background: rgba(255, 255, 255, 0.04);
        border: 1px solid rgba(255, 255, 255, 0.06);
      }
      .muted {
        color: #9db0c5;
      }
    </style>
  </head>
  <body>
    <h1>Genesis UI Mock Parent</h1>
    <p class="muted">Pipe: ${PIPE_NAME}</p>
    <div class="layout">
      <section class="panel">
        <label>
          Event
          <select id="event">
            <option value="world.select_object">world.select_object</option>
            <option value="edit_path.select_node">edit_path.select_node</option>
            <option value="edit_path.update_shape">edit_path.update_shape</option>
            <option value="edit_building.select_parcel">edit_building.select_parcel</option>
            <option value="system.ping">system.ping</option>
            <option value="system.shutdown">system.shutdown</option>
          </select>
        </label>
        <label>
          Window Type
          <select id="windowType">
            <option value="path_detail">path_detail</option>
            <option value="agent_detail">agent_detail</option>
            <option value="parcel_detail">parcel_detail</option>
            <option value="zone_detail">zone_detail</option>
            <option value="resource_detail">resource_detail</option>
            <option value="city_map">city_map</option>
            <option value="city_budget">city_budget</option>
            <option value="city_regulation">city_regulation</option>
            <option value="debug_data">debug_data</option>
            <option value="settings">settings</option>
            <option value="citizen_detail">citizen_detail</option>
            <option value="top_page">top_page</option>
          </select>
        </label>
        <label>
          Entity Type
          <input id="entityType" value="path" />
        </label>
        <label>
          Entity ID
          <input id="entityId" value="path-001" />
        </label>
        <label>
          Params JSON
          <textarea id="params">{
  "preferred_position": {
    "mode": "screen_point",
    "x": 240,
    "y": 180
  }
}</textarea>
        </label>
        <div class="row">
          <button id="send">Send</button>
          <button id="openSettings">Open Settings</button>
          <button id="shutdownTauri">Shutdown Tauri</button>
          <button id="exitMockParent">Exit Mock Parent</button>
        </div>
        <p id="status" class="muted"></p>
      </section>
      <section class="panel">
        <h2>IPC Log</h2>
        <div id="log" class="log"></div>
      </section>
    </div>

    <script>
      const log = document.getElementById("log");
      const status = document.getElementById("status");

      function addEntry(entry) {
        const el = document.createElement("div");
        el.className = "entry";
        el.innerHTML = "<strong>" + entry.type + "</strong><pre>" + escapeHtml(JSON.stringify(entry.payload, null, 2)) + "</pre>";
        log.prepend(el);
      }

      function escapeHtml(value) {
        return value
          .replaceAll("&", "&amp;")
          .replaceAll("<", "&lt;")
          .replaceAll(">", "&gt;");
      }

      async function send(event, payload) {
        status.textContent = "sending...";
        const response = await fetch("/api/send", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ event, payload })
        });
        const data = await response.json();
        status.textContent = data.ok ? "sent" : data.message;
      }

      async function exitMockParent() {
        status.textContent = "shutting down mock parent...";
        await fetch("/api/exit", { method: "POST" });
      }

      document.getElementById("send").addEventListener("click", async () => {
        const event = document.getElementById("event").value;
        const windowType = document.getElementById("windowType").value;
        const entityType = document.getElementById("entityType").value;
        const entityId = document.getElementById("entityId").value;
        const paramsText = document.getElementById("params").value.trim();
        const params = paramsText ? JSON.parse(paramsText) : {};
        const payload = {};

        if (event === "world.select_object") {
          payload.window_type = windowType;
          payload.entity = entityId ? { type: entityType, id: entityId } : undefined;
          payload.params = params;
        } else if (event === "edit_path.select_node" || event === "edit_path.update_shape" || event === "edit_building.select_parcel") {
          payload.data = params;
        }

        await send(event, payload);
      });

      document.getElementById("openSettings").addEventListener("click", async () => {
        await send("ui.open_window", {
          window_type: "settings",
          params: {}
        });
      });

      document.getElementById("shutdownTauri").addEventListener("click", async () => {
        await send("system.shutdown", {});
      });

      document.getElementById("exitMockParent").addEventListener("click", async () => {
        await exitMockParent();
      });

      const source = new EventSource("/events");
      source.onmessage = (event) => {
        const data = JSON.parse(event.data);
        addEntry(data);
      };
    </script>
  </body>
</html>`;
}
