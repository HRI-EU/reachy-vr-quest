// Smoke test of web/app.mjs against a fake DOM built from index.html's element ids.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { MockPC, MockWebSocket, flush, installGlobals } from "./mocks.mjs";

const html = readFileSync(new URL("../../phone_teleop/web/index.html", import.meta.url), "utf8");

// ---- timers we can clear at the end so node exits ----------------------------
const intervals = new Set();
const timeouts = new Set();
const realSetInterval = globalThis.setInterval;
const realSetTimeout = globalThis.setTimeout;
globalThis.setInterval = (fn, ms, ...a) => {
  const id = realSetInterval(fn, ms, ...a);
  intervals.add(id);
  return id;
};
globalThis.setTimeout = (fn, ms, ...a) => {
  const id = realSetTimeout(fn, ms, ...a);
  timeouts.add(id);
  return id;
};

// ---- fake DOM -------------------------------------------------------------------
class El {
  constructor(id, attrs = {}) {
    this.id = id;
    this.textContent = "";
    this.className = attrs.class ?? "";
    this.hidden = "hidden" in attrs;
    this.dataset = {};
    this.listeners = {};
    this.onclick = null;
    this.srcObject = null;
    const self = this;
    this.classList = {
      add: (c) => (self.className = [...new Set([...self.className.split(" ").filter(Boolean), c])].join(" ")),
      remove: (c) => (self.className = self.className.split(" ").filter((x) => x && x !== c).join(" ")),
      toggle: (c, on) => (on ? self.classList.add(c) : self.classList.remove(c)),
      contains: (c) => self.className.split(" ").includes(c),
    };
  }
  addEventListener(type, fn) {
    (this.listeners[type] ??= []).push(fn);
  }
  dispatch(type, ev = {}) {
    for (const fn of this.listeners[type] ?? []) fn(ev);
  }
  setPointerCapture() {}
  play() {
    return Promise.resolve();
  }
  click() {
    return this.onclick?.({});
  }
}

const elements = new Map();
for (const m of html.matchAll(/<(\w+)([^>]*)>/g)) {
  const attrs = {};
  for (const a of m[2].matchAll(/([\w-]+)(?:="([^"]*)")?/g)) attrs[a[1]] = a[2] ?? "";
  if (attrs.id) elements.set(attrs.id, new El(attrs.id, attrs));
  else if (attrs["data-action"]) {
    const el = new El(null, attrs);
    el.dataset.action = attrs["data-action"];
    elements.set(`action:${attrs["data-action"]}`, el);
  }
}
const lookedUp = new Set();
const docListeners = {};
const winListeners = {};
globalThis.document = {
  hidden: false,
  getElementById(id) {
    lookedUp.add(id);
    return elements.get(id) ?? null;
  },
  querySelectorAll(sel) {
    assert.equal(sel, "[data-action]");
    return [...elements.entries()].filter(([k]) => k.startsWith("action:")).map(([, v]) => v);
  },
  addEventListener(type, fn) {
    (docListeners[type] ??= []).push(fn);
  },
};
globalThis.window = globalThis;
globalThis.addEventListener = (type, fn) => (winListeners[type] ??= []).push(fn);
globalThis.isSecureContext = true;
globalThis.location = { host: "laptop.local:4443" };
let permissionAsked = 0;
globalThis.DeviceOrientationEvent = class {
  static async requestPermission() {
    permissionAsked++;
    return "granted";
  }
};
Object.defineProperty(globalThis, "navigator", { value: {}, configurable: true });
let rafCalls = 0;
globalThis.requestAnimationFrame = (cb) => {
  rafCalls++;
  return globalThis.setTimeout(() => cb(performance.now()), 16);
};
installGlobals();

const fire = (type, ev = {}) => (winListeners[type] ?? []).forEach((fn) => fn(ev));
const fireDoc = (type, ev = {}) => (docListeners[type] ?? []).forEach((fn) => fn(ev));

function cleanup() {
  for (const id of intervals) clearInterval(id);
  for (const id of timeouts) clearTimeout(id);
  for (const ws of MockWebSocket.instances) ws.close();
  MockPC.instances.forEach((p) => p.close());
}

test("app.mjs: start, stream, deadman, buttons, status", async () => {
  try {
    await body();
  } finally {
    cleanup(); // a failing assertion must not leave intervals running (node would hang)
  }
});

async function body() {
  await import("../../phone_teleop/web/app.mjs");
  const $ = (id) => elements.get(id);
  for (const id of ["btnStart"]) assert.ok($(id), id);

  await $("btnStart").click();
  await flush();
  assert.equal(permissionAsked, 1);
  assert.equal($("start").hidden, true);
  for (const id of lookedUp) assert.ok(elements.has(id), `getElementById("${id}") has no element in index.html`);

  let pose = MockWebSocket.instances.find((w) => w.url.endsWith("/pose"));
  const signal = MockWebSocket.instances.find((w) => w.url.endsWith("/signal"));
  assert.equal(pose.url, "wss://laptop.local:4443/pose");
  assert.equal(signal.url, "wss://laptop.local:4443/signal");
  pose.open();
  signal.open();
  assert.match($("chipServer").className, /ok/);
  // hello with a page id is the first message on every /pose connection
  const hello = pose.json()[0];
  assert.equal(hello.type, "hello");
  assert.ok(typeof hello.page === "string" && hello.page.length >= 8, hello.page);

  // orientation before held
  fire("deviceorientation", { alpha: 10, beta: 0, gamma: -90 });
  let last = pose.json().at(-1);
  assert.equal(last.type, "orient");
  assert.deepEqual([last.a, last.b, last.g, last.held, last.src], [10, 0, -90, false, "evt"]);
  // null alpha (no sensor) is ignored
  const n = pose.sent.length;
  fire("deviceorientation", { alpha: null, beta: null, gamma: null });
  assert.equal(pose.sent.length, n);

  // press and hold
  $("hold").dispatch("pointerdown", { pointerId: 1 });
  assert.equal(pose.json().at(-1).held, true);
  assert.equal(pose.json().at(-1).src, "ptr");
  fire("deviceorientation", { alpha: 12, beta: 1, gamma: -89 });
  assert.equal(pose.json().at(-1).held, true);
  // second finger, lift first: still held
  $("hold").dispatch("pointerdown", { pointerId: 2 });
  $("hold").dispatch("pointerup", { pointerId: 1 });
  assert.equal(pose.json().at(-1).held, true);
  $("hold").dispatch("pointerup", { pointerId: 2 });
  assert.equal(pose.json().at(-1).held, false);

  // keepalive resends while still
  $("hold").dispatch("pointerdown", { pointerId: 3 });
  const before = pose.sent.length;
  await new Promise((r) => realSetTimeout(r, 150));
  assert.ok(pose.sent.length >= before + 2, "keepalive did not resend samples");
  assert.equal(pose.json().at(-1).src, "keepalive");
  assert.equal(pose.json().at(-1).held, true);
  // app goes to background -> release
  document.hidden = true;
  fireDoc("visibilitychange");
  document.hidden = false;
  assert.deepEqual(pose.json().at(-1), { type: "release" });
  await new Promise((r) => realSetTimeout(r, 80));
  assert.equal(pose.json().filter((m) => m.type === "orient").at(-1).held, false);

  // seq increases
  const seqs = pose.json().filter((m) => m.type === "orient").map((m) => m.seq);
  for (let i = 1; i < seqs.length; i++) assert.ok(seqs[i] > seqs[i - 1]);

  // Center: fresh sample + calibrate
  $("btnCenter").click();
  const tail = pose.json().slice(-2);
  assert.equal(tail[0].type, "orient");
  assert.equal(tail[0].src, "center");
  assert.deepEqual(tail[1], { type: "calibrate" });
  pose.receive({ type: "calibrated", ok: false, error: "Hold the phone upright" });
  assert.equal($("toast").textContent, "Hold the phone upright");

  // actions
  elements.get("action:wake_up").click();
  assert.deepEqual(pose.json().at(-1), { type: "action", name: "wake_up" });
  elements.get("action:motors_off").click();
  assert.deepEqual(pose.json().at(-1), { type: "action", name: "motors_off" });

  // status chips
  const base = { calibrated: true, engaged: true, synced: true, limited: false, catching_up: false, blocked: "",
    robot: true, motors: "enabled", moving: false, head_rpy_deg: [1, 2, 3], body_yaw_deg: 0, error: "", sent: 1, rx: 2 };
  pose.receive({ type: "status", ...base });
  assert.match($("chipRobot").className, /ok/);
  assert.equal($("chipMove").textContent, "moving");
  assert.ok($("hold").classList.contains("active"));
  pose.receive({ type: "status", ...base, catching_up: true });
  assert.equal($("chipMove").textContent, "catching up");
  pose.receive({ type: "status", ...base, synced: false, blocked: "robot move running", moving: true });
  assert.equal($("chipMove").textContent, "blocked: robot move running");
  assert.equal($("chipRobot").textContent, "robot busy");
  assert.ok(!$("hold").classList.contains("active"));
  pose.receive({ type: "status", ...base, synced: false, blocked: "motors disabled", motors: "disabled" });
  assert.equal($("chipRobot").textContent, "motors disabled");
  pose.receive({ type: "status", calibrated: false, engaged: false, synced: false, robot: false });
  assert.equal($("chipMove").textContent, "tap Center");
  assert.ok(!$("hold").classList.contains("active"));

  // laptop-synced clock from pong.server_t
  for (let i = 0; i < 150 && !pose.json().some((m) => m.type === "ping"); i++) {
    await new Promise((r) => realSetTimeout(r, 10));
  }
  const pingMsg = pose.json().filter((m) => m.type === "ping").at(-1);
  assert.ok(pingMsg, "no ping sent");
  const serverNow = performance.timeOrigin + performance.now() + 123456; // laptop clock ahead by ~123 s
  pose.receive({ type: "pong", t: performance.now() - 2, server_t: serverNow });

  // stats overlay + lag clock render without throwing; rAF only while visible
  $("btnStats").click();
  assert.equal($("stats").hidden, false);
  assert.equal($("lagClock").hidden, false);
  await new Promise((r) => realSetTimeout(r, 600));
  assert.match($("stats").textContent, /orient/);
  assert.match($("lagClock").textContent, /^\d{4}$/);
  $("btnStats").click();
  assert.equal($("lagClock").hidden, true);
  await new Promise((r) => realSetTimeout(r, 60));
  const raf = rafCalls;
  await new Promise((r) => realSetTimeout(r, 100));
  assert.equal(rafCalls, raf, "lag clock keeps animating while hidden");

  // menu toggle
  const wasHidden = $("menu").hidden;
  $("btnMenu").click();
  assert.equal($("menu").hidden, !wasHidden);

  // video toggle
  $("btnVideo").click();
  assert.equal($("btnVideo").textContent, "Video on");
  assert.ok(signal.closed);
  $("btnVideo").click();
  assert.equal($("btnVideo").textContent, "Video off");

  // reconnect of the same page sends the same page id; resumed toast
  const poseCount = MockWebSocket.instances.filter((w) => w.url.endsWith("/pose")).length;
  pose.serverClose(1006);
  await new Promise((r) => realSetTimeout(r, 1100));
  const poses = MockWebSocket.instances.filter((w) => w.url.endsWith("/pose"));
  assert.equal(poses.length, poseCount + 1);
  const pose2 = poses.at(-1);
  pose2.open();
  assert.deepEqual(pose2.json()[0], hello);
  pose2.receive({ type: "hello", resumed: true });
  assert.match($("toast").textContent, /still centered/);
  pose = pose2;

  // kicked: no reconnect
  pose.receive({ type: "kicked" });
  const count = MockWebSocket.instances.filter((w) => w.url.endsWith("/pose")).length;
  pose.serverClose(4000);
  assert.equal($("chipServer").textContent, "taken over");
  await new Promise((r) => realSetTimeout(r, 1100));
  assert.equal(MockWebSocket.instances.filter((w) => w.url.endsWith("/pose")).length, count);

  // Stats on -> off -> on within one frame must not leave two lag-clock rAF loops running.
  assert.equal($("stats").hidden, true);
  $("btnStats").click();
  $("btnStats").click();
  $("btnStats").click();
  await new Promise((r) => realSetTimeout(r, 50));
  const r0 = rafCalls;
  await new Promise((r) => realSetTimeout(r, 320));
  const perSec = (rafCalls - r0) / 0.32;
  assert.ok(perSec < 80, `lag clock runs ${perSec.toFixed(0)} rAF/s (expected ~60: one loop)`);
}
