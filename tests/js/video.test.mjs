// Signaling state machine of web/video.mjs vs Assets/Scripts/Runtime/Transport/WebRTCClient.cs.
import test from "node:test";
import assert from "node:assert/strict";
import { MockPC, MockWebSocket, deferred, flush, installGlobals } from "./mocks.mjs";

installGlobals();
const { ReachyVideo } = await import("../../phone_teleop/web/video.mjs");

function videoEl() {
  return { srcObject: null, play: () => Promise.resolve() };
}

function setup() {
  MockWebSocket.instances.length = 0;
  MockPC.instances.length = 0;
  const states = [];
  const el = videoEl();
  const v = new ReachyVideo("wss://host/signal", el, (s) => states.push(s));
  v.start();
  const ws = MockWebSocket.instances.at(-1);
  ws.open();
  return { v, ws, el, states };
}

const PRODUCERS = {
  type: "list",
  producers: [
    { id: "p-other", meta: { name: "something" } },
    { id: "p-reachy", meta: { name: "reachymini" } },
  ],
};

test("handshake: welcome -> setPeerStatus listener + list -> startSession -> answer", async () => {
  const { v, ws, el } = setup();
  assert.equal(ws.url, "wss://host/signal");
  ws.receive({ type: "welcome", peerId: "me" });
  assert.deepEqual(ws.json(), [
    { type: "setPeerStatus", roles: ["listener"], meta: { name: "iphone-teleop" } },
    { type: "list" },
  ]);
  ws.receive(PRODUCERS);
  assert.deepEqual(ws.json().at(-1), { type: "startSession", peerId: "p-reachy" });
  // A second list (e.g. after peerStatusChanged) must not start another session.
  ws.receive(PRODUCERS);
  ws.receive({ type: "peerStatusChanged", peerId: "x", roles: ["producer"] });
  assert.equal(ws.json().filter((m) => m.type === "startSession").length, 1);
  assert.equal(ws.json().filter((m) => m.type === "list").length, 1);

  ws.receive({ type: "sessionStarted", peerId: "p-reachy", sessionId: "S1" });
  assert.equal(v.sessionId, "S1");

  ws.receive({ type: "peer", sessionId: "S1", sdp: { type: "offer", sdp: "v=0 offer" } });
  await flush();
  const pc = MockPC.instances[0];
  assert.deepEqual(pc.config.iceServers, []); // LAN only: no STUN (robot and phone share the network)
  assert.deepEqual(pc.calls[0], ["setRemoteDescription", { type: "offer", sdp: "v=0 offer" }]);
  assert.deepEqual(ws.json().at(-1), { type: "peer", sessionId: "S1", sdp: { type: "answer", sdp: "v=0 answer" } });

  // local ICE -> peer message with sessionId
  pc.onicecandidate({ candidate: { candidate: "candidate:1 1 udp 1 1.2.3.4 5 typ host", sdpMLineIndex: 0 } });
  assert.deepEqual(ws.json().at(-1), {
    type: "peer",
    sessionId: "S1",
    ice: { candidate: "candidate:1 1 udp 1 1.2.3.4 5 typ host", sdpMLineIndex: 0 },
  });
  const n = ws.sent.length;
  pc.onicecandidate({ candidate: null }); // end of candidates
  assert.equal(ws.sent.length, n);

  // track -> video element
  const track = { kind: "video" };
  pc.ontrack({ track, streams: [], receiver: {} });
  assert.deepEqual(el.srcObject.tracks, [track]);
  pc.ontrack({ track: { kind: "audio" }, streams: [], receiver: {} });
  assert.deepEqual(el.srcObject.tracks, [track]);

  v.stop();
  // stop sends endSession and closes
  assert.deepEqual(ws.json().at(-1), { type: "endSession", sessionId: "S1" });
  assert.ok(ws.closed && pc.closed);
});

test("waits for a producer: empty list, then peerStatusChanged re-lists", () => {
  const { v, ws, states } = setup();
  ws.receive({ type: "welcome" });
  ws.receive({ type: "list", producers: [] });
  assert.ok(states.includes("waiting for robot camera"));
  ws.receive({ type: "peerStatusChanged", roles: ["producer"] });
  assert.deepEqual(ws.json().at(-1), { type: "list" });
  ws.receive({ type: "list", producers: [{ id: "only" }] });
  assert.deepEqual(ws.json().at(-1), { type: "startSession", peerId: "only" });
  v.stop();
});

test("remote ICE is queued until the remote description is set", async () => {
  const { v, ws } = setup();
  ws.receive({ type: "welcome" });
  ws.receive(PRODUCERS);
  ws.receive({ type: "sessionStarted", sessionId: "S" });
  // ICE before any offer
  ws.receive({ type: "peer", sessionId: "S", ice: { candidate: "c0", sdpMLineIndex: 0 } });
  await flush();
  const gate = deferred();
  // Offer arrives; hold setRemoteDescription open
  const origEnsure = v._ensurePc.bind(v);
  v._ensurePc = () => {
    const pc = origEnsure();
    pc.remoteGate = gate;
    return pc;
  };
  ws.receive({ type: "peer", sessionId: "S", sdp: { type: "offer", sdp: "o" } });
  ws.receive({ type: "peer", sessionId: "S", ice: { candidate: "c1", sdpMLineIndex: 1 } });
  await flush();
  const pc = MockPC.instances[0];
  assert.equal(pc.calls.filter((c) => c[0] === "addIceCandidate").length, 0);
  gate.resolve();
  await flush();
  await flush();
  ws.receive({ type: "peer", sessionId: "S", ice: { candidate: "c2" } });
  await flush();
  const ice = pc.calls.filter((c) => c[0] === "addIceCandidate").map((c) => c[1]);
  assert.deepEqual(ice, [
    { candidate: "c0", sdpMLineIndex: 0 },
    { candidate: "c1", sdpMLineIndex: 1 },
    { candidate: "c2", sdpMLineIndex: 0 },
  ]);
  // answer still sent once
  assert.equal(ws.json().filter((m) => m.sdp?.type === "answer").length, 1);
  v.stop();
});

test("sessionId from peer message is adopted (like WebRTCClient.cs)", async () => {
  const { v, ws } = setup();
  ws.receive({ type: "welcome" });
  ws.receive(PRODUCERS);
  ws.receive({ type: "peer", sessionId: "S9", sdp: { type: "offer", sdp: "o" } });
  await flush();
  assert.equal(ws.json().at(-1).sessionId, "S9");
  v.stop();
});

test("invalid JSON and unknown types are ignored", () => {
  const { v, ws } = setup();
  ws.receive("{not json");
  ws.receive({ type: "whatever" });
  ws.receive({ type: "error", details: "oops" });
  assert.equal(ws.sent.length, 0);
  v.stop();
});

test("endSession / socket close / negotiation failure -> retry; stop() cancels the retry", async () => {
  const { v, ws, states } = setup();
  ws.receive({ type: "welcome" });
  ws.receive(PRODUCERS);
  ws.receive({ type: "sessionStarted", sessionId: "S" });
  ws.receive({ type: "endSession", sessionId: "S" });
  assert.ok(states.some((s) => s.startsWith("retrying (robot ended session)")));
  assert.ok(ws.closed);
  assert.ok(v.retryTimer);
  v.stop();
  assert.equal(states.at(-1), "off");

  const b = setup();
  b.ws.serverClose();
  assert.ok(b.states.some((s) => s.startsWith("retrying (signaling closed)")));
  b.v.stop();

  const c = setup();
  c.ws.receive({ type: "welcome" });
  c.ws.receive(PRODUCERS);
  const orig = c.v._ensurePc.bind(c.v);
  c.v._ensurePc = () => {
    const pc = orig();
    pc.failRemote = true;
    return pc;
  };
  c.ws.receive({ type: "peer", sessionId: "S", sdp: { type: "offer", sdp: "bad" } });
  await flush();
  assert.ok(c.states.some((s) => s.startsWith("retrying (negotiation: bad sdp)")));
  c.v.stop();
});

test("retry reconnects after the delay", async (t) => {
  const { v, ws } = setup();
  const before = MockWebSocket.instances.length;
  ws.serverClose();
  await new Promise((r) => setTimeout(r, 2100));
  assert.equal(MockWebSocket.instances.length, before + 1);
  v.stop();
});

test("pc failed -> retry", async () => {
  const { v, ws, states } = setup();
  ws.receive({ type: "welcome" });
  ws.receive(PRODUCERS);
  ws.receive({ type: "peer", sessionId: "S", sdp: { type: "offer", sdp: "o" } });
  await flush();
  const pc = MockPC.instances[0];
  pc.connectionState = "failed";
  pc.onconnectionstatechange();
  assert.ok(states.includes("failed"));
  assert.ok(states.some((s) => s.startsWith("retrying (connection failed)")));
  assert.ok(pc.closed);
  v.stop();
});

test("stale negotiation from a torn-down session does not kill the new one", async () => {
  // Offer negotiation still in flight when the robot ends the session and we reconnect.
  const { v, ws } = setup();
  ws.receive({ type: "welcome" });
  ws.receive(PRODUCERS);
  const gate = deferred();
  const orig = v._ensurePc.bind(v);
  v._ensurePc = () => {
    const pc = orig();
    pc.remoteGate = gate;
    return pc;
  };
  ws.receive({ type: "peer", sessionId: "S", sdp: { type: "offer", sdp: "o" } });
  await flush();
  ws.receive({ type: "endSession", sessionId: "S" }); // tears down pc (closed)
  clearTimeout(v.retryTimer);
  v._connect(); // the retry fires: new socket
  const ws2 = MockWebSocket.instances.at(-1);
  ws2.open();
  ws2.receive({ type: "welcome" });
  gate.resolve(); // old setRemoteDescription now rejects ("closed")
  await flush();
  await flush();
  assert.equal(v.ws, ws2, "the new signaling socket was torn down by the old session's error");
  v.stop();
});
