// Minimal browser mocks: WebSocket, RTCPeerConnection, MediaStream, timers.

export class MockWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;
  static instances = [];

  constructor(url) {
    this.url = url;
    this.readyState = MockWebSocket.CONNECTING;
    this.sent = [];
    this.closed = false;
    this.onopen = this.onclose = this.onmessage = this.onerror = null;
    MockWebSocket.instances.push(this);
  }
  send(data) {
    if (this.readyState !== MockWebSocket.OPEN) throw new Error("InvalidStateError: not open");
    this.sent.push(data);
  }
  close() {
    if (this.closed) return;
    this.closed = true;
    this.readyState = MockWebSocket.CLOSED;
    queueMicrotask(() => this.onclose?.({ code: 1000 }));
  }
  // test helpers
  open() {
    this.readyState = MockWebSocket.OPEN;
    this.onopen?.({});
  }
  receive(obj) {
    this.onmessage?.({ data: typeof obj === "string" ? obj : JSON.stringify(obj) });
  }
  serverClose(code = 1006) {
    this.closed = true;
    this.readyState = MockWebSocket.CLOSED;
    this.onclose?.({ code });
  }
  json() {
    return this.sent.map((s) => JSON.parse(s));
  }
}

export function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

export class MockPC {
  static instances = [];
  constructor(config) {
    this.config = config;
    this.calls = [];
    this.remote = null;
    this.local = null;
    this.closed = false;
    this.connectionState = "new";
    this.remoteGate = null; // set to a deferred to hold setRemoteDescription
    this.failRemote = false;
    MockPC.instances.push(this);
  }
  async setRemoteDescription(desc) {
    this.calls.push(["setRemoteDescription", desc]);
    if (this.remoteGate) await this.remoteGate.promise;
    if (this.failRemote) throw new Error("bad sdp");
    if (this.closed) throw new Error("closed");
    this.remote = desc;
  }
  async createAnswer() {
    this.calls.push(["createAnswer"]);
    return { type: "answer", sdp: "v=0 answer" };
  }
  async setLocalDescription(desc) {
    this.calls.push(["setLocalDescription", desc]);
    if (this.closed) throw new Error("closed");
    this.local = desc;
  }
  async addIceCandidate(c) {
    if (!this.remote) throw new Error("addIceCandidate before remote description");
    this.calls.push(["addIceCandidate", c]);
  }
  close() {
    this.closed = true;
  }
  async getStats() {
    return new Map();
  }
}

export class MockMediaStream {
  constructor(tracks) {
    this.tracks = tracks;
  }
}

export function installGlobals() {
  globalThis.WebSocket = MockWebSocket;
  globalThis.RTCPeerConnection = MockPC;
  globalThis.MediaStream = MockMediaStream;
}

export const flush = () => new Promise((r) => setImmediate(r));
