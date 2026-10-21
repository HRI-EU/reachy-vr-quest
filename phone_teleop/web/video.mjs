// Receive-only WebRTC client for the Reachy daemon's GStreamer signaling server.
// JS port of Assets/Scripts/Runtime/Transport/WebRTCClient.cs (the Quest app).

import { chooseProducer } from "./lib.mjs";

const RETRY_MS = 2000;

export class ReachyVideo {
  constructor(url, videoEl, onState = () => {}) {
    this.url = url;
    this.video = videoEl;
    this.onState = onState;
    this.ws = null;
    this.pc = null;
    this.sessionId = null;
    this.producerId = null;
    this.pendingIce = [];
    this.remoteSet = false;
    this.stopped = true;
    this.retryTimer = null;
    this.lastStats = null;
    this.generation = 0; // bumped on every teardown; stale async work checks it
  }

  start() {
    this.stopped = false;
    this._connect();
  }

  stop() {
    this.stopped = true;
    clearTimeout(this.retryTimer);
    this._teardown();
    this.onState("off");
  }

  _connect() {
    this._teardown();
    this.onState("connecting");
    const ws = new WebSocket(this.url);
    this.ws = ws;
    ws.onmessage = (e) => this._onMessage(e.data);
    ws.onclose = () => {
      if (this.ws === ws) this._retry("signaling closed");
    };
    ws.onerror = () => {};
  }

  _retry(reason) {
    this._teardown();
    if (this.stopped) return;
    this.onState(`retrying (${reason})`);
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => this._connect(), RETRY_MS);
  }

  _teardown() {
    this.generation++;
    if (this.ws) {
      const ws = this.ws;
      this.ws = null;
      if (this.sessionId && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: "endSession", sessionId: this.sessionId }));
      }
      ws.close();
    }
    if (this.pc) {
      this.pc.close();
      this.pc = null;
    }
    this.sessionId = null;
    this.producerId = null;
    this.pendingIce = [];
    this.remoteSet = false;
    this.lastStats = null;
    if (this.video) this.video.srcObject = null;
  }

  _send(msg) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(msg));
  }

  _onMessage(raw) {
    let msg;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }
    switch (msg.type) {
      case "welcome":
        this._send({ type: "setPeerStatus", roles: ["listener"], meta: { name: "iphone-teleop" } });
        this._send({ type: "list" });
        break;
      case "list":
        if (!this.producerId) {
          this.producerId = chooseProducer(msg.producers);
          if (this.producerId) this._send({ type: "startSession", peerId: this.producerId });
          else this.onState("waiting for robot camera");
        }
        break;
      case "peerStatusChanged":
        if (!this.producerId) this._send({ type: "list" });
        break;
      case "sessionStarted":
        this.sessionId = msg.sessionId;
        break;
      case "peer":
        this._onPeer(msg);
        break;
      case "endSession":
        this._retry("robot ended session");
        break;
      case "error":
        this.onState(`signaling error: ${msg.details ?? ""}`);
        break;
    }
  }

  _ensurePc() {
    if (this.pc) return this.pc;
    // LAN only: host candidates are enough, and no STUN keeps it working offline.
    const pc = new RTCPeerConnection({ iceServers: [] });
    pc.onicecandidate = (e) => {
      if (!e.candidate || !e.candidate.candidate || !this.sessionId) return;
      this._send({
        type: "peer",
        sessionId: this.sessionId,
        ice: { candidate: e.candidate.candidate, sdpMLineIndex: e.candidate.sdpMLineIndex ?? 0 },
      });
    };
    pc.ontrack = (e) => {
      if (e.track.kind !== "video") return;
      try {
        if ("jitterBufferTarget" in e.receiver) e.receiver.jitterBufferTarget = 0;
      } catch {}
      this.video.srcObject = e.streams[0] ?? new MediaStream([e.track]);
      this.video.play().catch(() => {});
    };
    pc.onconnectionstatechange = () => {
      this.onState(pc.connectionState);
      if (pc.connectionState === "failed") this._retry("connection failed");
    };
    this.pc = pc;
    return pc;
  }

  async _onPeer(msg) {
    if (msg.sessionId) this.sessionId = msg.sessionId;
    const gen = this.generation;
    const stale = () => gen !== this.generation;
    try {
      if (msg.sdp && msg.sdp.type === "offer") {
        const pc = this._ensurePc();
        await pc.setRemoteDescription({ type: "offer", sdp: msg.sdp.sdp });
        if (stale()) return;
        this.remoteSet = true;
        for (const ice of this.pendingIce.splice(0)) await this._addIce(ice);
        const answer = await pc.createAnswer();
        if (stale()) return;
        await pc.setLocalDescription(answer);
        if (stale()) return;
        this._send({ type: "peer", sessionId: this.sessionId, sdp: { type: "answer", sdp: answer.sdp } });
      }
      if (msg.ice && msg.ice.candidate) {
        if (this.remoteSet) await this._addIce(msg.ice);
        else this.pendingIce.push(msg.ice);
      }
    } catch (err) {
      if (stale()) return; // error from a session that was already torn down
      this._retry(`negotiation: ${err.message ?? err}`);
    }
  }

  async _addIce(ice) {
    if (!this.pc) return;
    try {
      await this.pc.addIceCandidate({ candidate: ice.candidate, sdpMLineIndex: ice.sdpMLineIndex ?? 0 });
    } catch (err) {
      console.warn("addIceCandidate", err);
    }
  }

  /** Video receive stats since the previous call: fps, size, avg jitter-buffer delay. */
  async stats() {
    if (!this.pc) return null;
    const report = await this.pc.getStats();
    let inbound = null;
    report.forEach((s) => {
      if (s.type === "inbound-rtp" && s.kind === "video") inbound = s;
    });
    if (!inbound) return null;
    const prev = this.lastStats;
    this.lastStats = inbound;
    let jbMs = null;
    if (prev && inbound.jitterBufferEmittedCount > prev.jitterBufferEmittedCount) {
      jbMs = (1000 * (inbound.jitterBufferDelay - prev.jitterBufferDelay)) /
        (inbound.jitterBufferEmittedCount - prev.jitterBufferEmittedCount);
    }
    return {
      fps: inbound.framesPerSecond ?? null,
      width: inbound.frameWidth ?? null,
      height: inbound.frameHeight ?? null,
      jitterBufferMs: jbMs,
      lost: inbound.packetsLost ?? 0,
    };
  }
}
