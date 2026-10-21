import { ClockSync, Deadman, RateMeter, orientMessage } from "./lib.mjs";
import { ReachyVideo } from "./video.mjs";

const $ = (id) => document.getElementById(id);
const wsBase = `wss://${location.host}`;

// Resend the last sample this often if iOS goes quiet (phone perfectly still),
// so a still-but-held phone doesn't trip the server's deadman timeout.
const KEEPALIVE_MS = 50;
const PING_MS = 1000;

// Identifies this page load: the server keeps the calibration across reconnects of the
// same page (same iOS alpha reference) and clears it for a reload or another phone.
const pageId = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
const epochNow = () => performance.timeOrigin + performance.now();

const deadman = new Deadman();
const clock = new ClockSync();
const orientRate = new RateMeter();
let pose = null;
let poseOpen = false;
let kicked = false;
let seq = 0;
let lastOrient = null; // {alpha, beta, gamma}
let lastSendAt = 0;
let rttMs = null;
let serverStatus = null;
let video = null;
let videoState = "off";
let statsVisible = false;

function toast(text, ms = 2200) {
  const el = $("toast");
  el.textContent = text;
  el.classList.add("show");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.remove("show"), ms);
}

function setChip(id, text, cls) {
  const el = $(id);
  el.textContent = text;
  el.className = `chip ${cls ?? ""}`;
}

// ---- /pose channel ----------------------------------------------------------
function sendPose(msg) {
  if (pose && poseOpen) {
    pose.send(JSON.stringify(msg));
    return true;
  }
  return false;
}

function sendOrient(src) {
  if (!lastOrient) return;
  const msg = orientMessage(lastOrient, deadman.held, seq++, performance.now(), src);
  if (msg && sendPose(msg)) lastSendAt = performance.now();
}

function connectPose() {
  const ws = new WebSocket(`${wsBase}/pose`);
  pose = ws;
  ws.onopen = () => {
    poseOpen = true;
    ws.send(JSON.stringify({ type: "hello", page: pageId }));
    setChip("chipServer", "server", "ok");
  };
  ws.onclose = () => {
    if (pose !== ws) return;
    poseOpen = false;
    serverStatus = null;
    setChip("chipServer", kicked ? "taken over" : "server", "bad");
    setChip("chipRobot", "robot", "bad");
    if (!kicked) setTimeout(connectPose, 1000);
  };
  ws.onmessage = (e) => {
    let msg;
    try {
      msg = JSON.parse(e.data);
    } catch {
      return;
    }
    if (msg.type === "status") onStatus(msg);
    else if (msg.type === "pong") {
      rttMs = performance.now() - msg.t;
      clock.update(performance.timeOrigin + msg.t, epochNow(), msg.server_t);
    } else if (msg.type === "hello" && msg.resumed) toast("Reconnected (still centered)");
    else if (msg.type === "calibrated") toast(msg.ok ? "Centered" : msg.error);
    else if (msg.type === "action") toast(msg.ok ? `${msg.name}: ok` : `${msg.name} failed: ${msg.error}`);
    else if (msg.type === "kicked") {
      kicked = true;
      toast("Another phone took control", 4000);
    }
  };
}

function onStatus(st) {
  serverStatus = st;
  if (!st.robot) setChip("chipRobot", "robot", "bad");
  else if (st.motors !== "enabled") setChip("chipRobot", `motors ${st.motors ?? "?"}`, "warn");
  else if (st.moving) setChip("chipRobot", "robot busy", "warn");
  else setChip("chipRobot", "robot", "ok");
  let move;
  if (!st.calibrated) move = ["tap Center", "warn"];
  else if (st.engaged && st.blocked) move = [`blocked: ${st.blocked}`, "bad"];
  else if (st.engaged && st.synced && st.catching_up) move = ["catching up", "warn"];
  else if (st.engaged && st.synced) move = [st.limited ? "moving · limit" : "moving", st.limited ? "warn" : "ok"];
  else if (st.engaged) move = ["syncing…", "warn"];
  else move = ["idle", ""];
  setChip("chipMove", ...move);
  $("hold").classList.toggle("active", Boolean(st.engaged && st.synced));
}

// ---- inputs -----------------------------------------------------------------
function onOrientation(e) {
  if (e.alpha === null) return;
  orientRate.tick(performance.now());
  lastOrient = { alpha: e.alpha, beta: e.beta, gamma: e.gamma, timeStamp: e.timeStamp };
  sendOrient("evt");
}

function setupDeadman() {
  const hold = $("hold");
  const changed = () => sendOrient("ptr");
  hold.addEventListener("pointerdown", (e) => {
    hold.setPointerCapture?.(e.pointerId);
    deadman.down(e.pointerId);
    changed();
  });
  for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) {
    hold.addEventListener(type, (e) => {
      deadman.up(e.pointerId);
      changed();
    });
  }
  const releaseAll = () => {
    deadman.clear();
    sendPose({ type: "release" });
  };
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) releaseAll();
  });
  window.addEventListener("pagehide", releaseAll);
  window.addEventListener("blur", releaseAll);
}

function setupButtons() {
  $("btnCenter").onclick = () => {
    if (!lastOrient) return toast("No orientation data yet");
    sendOrient("center"); // make sure the server has the freshest sample
    sendPose({ type: "calibrate" });
  };
  $("btnMenu").onclick = () => ($("menu").hidden = !$("menu").hidden);
  for (const btn of document.querySelectorAll("[data-action]")) {
    btn.onclick = () => sendPose({ type: "action", name: btn.dataset.action }) || toast("Not connected");
  }
  $("btnStats").onclick = () => {
    statsVisible = !statsVisible;
    $("stats").hidden = !statsVisible;
    $("lagClock").hidden = !statsVisible;
    if (statsVisible) requestAnimationFrame(drawLagClock);
  };
  $("btnVideo").onclick = () => {
    if (videoState === "off") {
      video.start();
      $("btnVideo").textContent = "Video off";
    } else {
      video.stop();
      $("btnVideo").textContent = "Video on";
    }
  };
}

// ---- stats ------------------------------------------------------------------
// Laptop-synced ms clock (mod 10 s). Point the robot camera at /clock on the laptop: one
// iPhone screenshot then shows both, and lag = this clock - the clock seen in the video.
function drawLagClock() {
  if (!statsVisible) return;
  const t = clock.serverNow(epochNow());
  $("lagClock").textContent = t === null ? "sync…" : String(Math.floor(t) % 10000).padStart(4, "0");
  requestAnimationFrame(drawLagClock);
}

const fmt = (v, d = 0) => (v === null || v === undefined || Number.isNaN(v) ? "–" : Number(v).toFixed(d));

async function renderStats() {
  if (!statsVisible) return;
  const o = orientRate.stats(performance.now());
  const v = video ? await video.stats() : null;
  const st = serverStatus ?? {};
  const rpy = st.head_rpy_deg ?? [];
  const raw = lastOrient ?? {};
  $("stats").textContent = [
    `orient  ${fmt(o.hz, 1)} Hz  gap ${fmt(o.meanMs, 1)}±${fmt(o.jitterMs, 1)} max ${fmt(o.maxMs)} ms`,
    `raw     a ${fmt(raw.alpha, 1)}  b ${fmt(raw.beta, 1)}  g ${fmt(raw.gamma, 1)}`,
    `rtt     ${fmt(rttMs, 1)} ms (phone↔laptop, best ${fmt(clock.bestRtt, 1)})`,
    `video   ${videoState}  ${fmt(v?.fps)} fps  ${v?.width ?? "–"}x${v?.height ?? "–"}  jb ${fmt(v?.jitterBufferMs)} ms  lost ${v?.lost ?? "–"}`,
    `cmd     r ${fmt(rpy[0], 1)}  p ${fmt(rpy[1], 1)}  y ${fmt(rpy[2], 1)}  body ${fmt(st.body_yaw_deg, 1)}`,
    `robot   motors ${st.motors ?? "–"}  move ${st.moving ? "running" : "–"}  ${st.blocked ? "blocked: " + st.blocked : ""}`,
    `server  rx ${st.rx ?? "–"}  sent ${st.sent ?? "–"}  ${st.error ?? ""}`,
  ].join("\n");
}

// ---- start ------------------------------------------------------------------
async function requestOrientationPermission() {
  const DOE = window.DeviceOrientationEvent;
  if (!DOE) throw new Error("This browser has no DeviceOrientation support.");
  if (!window.isSecureContext) throw new Error("Page is not HTTPS: install and trust the certificate first.");
  if (typeof DOE.requestPermission === "function") {
    const result = await DOE.requestPermission();
    if (result !== "granted") throw new Error("Motion access denied. Reload and tap Allow.");
  }
}

async function keepAwake() {
  try {
    if ("wakeLock" in navigator) {
      const lock = await navigator.wakeLock.request("screen");
      document.addEventListener("visibilitychange", () => {
        if (!document.hidden && lock.released) keepAwake();
      }, { once: true });
    }
  } catch {}
}

$("btnStart").onclick = async () => {
  try {
    await requestOrientationPermission();
  } catch (err) {
    const el = $("startError");
    el.textContent = err.message;
    el.hidden = false;
    return;
  }
  $("start").hidden = true;
  window.addEventListener("deviceorientation", onOrientation);
  setupDeadman();
  setupButtons();
  connectPose();
  video = new ReachyVideo(`${wsBase}/signal`, $("video"), (state) => {
    videoState = state;
    const cls = state === "connected" ? "ok" : state === "off" || state.startsWith("retrying") ? "bad" : "warn";
    setChip("chipVideo", state === "connected" ? "video" : `video: ${state}`, cls);
  });
  video.start();
  $("video").play().catch(() => {});
  keepAwake();
  setInterval(() => sendPose({ type: "ping", t: performance.now() }), PING_MS);
  setInterval(() => {
    if (performance.now() - lastSendAt > KEEPALIVE_MS) sendOrient("keepalive");
  }, KEEPALIVE_MS / 2);
  setInterval(renderStats, 500);
  setTimeout(() => { if (!lastOrient) toast("No orientation events: is Motion & Orientation Access on?", 5000); }, 2000);
};
