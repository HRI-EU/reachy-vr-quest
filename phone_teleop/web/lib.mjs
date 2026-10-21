// Small pure helpers, shared by the page and the Node tests.

/** Events-per-second meter with interval jitter, over a sliding window. */
export class RateMeter {
  constructor(windowMs = 2000) {
    this.windowMs = windowMs;
    this.times = [];
  }
  tick(now) {
    this.times.push(now);
    const cutoff = now - this.windowMs;
    while (this.times.length && this.times[0] < cutoff) this.times.shift();
  }
  stats(now) {
    const t = this.times.filter((x) => x >= now - this.windowMs);
    if (t.length < 2) return { hz: 0, meanMs: 0, jitterMs: 0, maxMs: 0 };
    const gaps = t.slice(1).map((x, i) => x - t[i]);
    const mean = gaps.reduce((a, b) => a + b, 0) / gaps.length;
    const variance = gaps.reduce((a, b) => a + (b - mean) ** 2, 0) / gaps.length;
    return { hz: 1000 / mean, meanMs: mean, jitterMs: Math.sqrt(variance), maxMs: Math.max(...gaps) };
  }
}

/** Pick the robot's camera producer from a GStreamer signaling "list" message. */
export function chooseProducer(producers, preferredName = "reachymini") {
  if (!Array.isArray(producers) || producers.length === 0) return null;
  const preferred = producers.find(
    (p) => String(p?.meta?.name ?? "").toLowerCase() === preferredName.toLowerCase(),
  );
  const chosen = preferred ?? producers[0];
  return chosen && chosen.id ? chosen.id : null;
}

/**
 * Build the /pose "orient" message, or null if the event carries no usable angles.
 * src: "evt" for a real deviceorientation event, else why the last sample is re-sent
 * ("keepalive", "ptr", "center"); et: the event's own timestamp (ms).
 */
export function orientMessage(event, held, seq, t, src = "evt") {
  const { alpha, beta, gamma, timeStamp } = event ?? {};
  if (![alpha, beta, gamma].every((v) => typeof v === "number" && Number.isFinite(v))) return null;
  const msg = { type: "orient", a: alpha, b: beta, g: gamma, held: held === true, seq, t, src };
  if (typeof timeStamp === "number") msg.et = timeStamp;
  return msg;
}

/**
 * Laptop clock estimate from ping/pong: offset = server_t - (send + rtt/2), keeping the
 * sample with the lowest RTT (least asymmetric delay). Times are epoch ms.
 */
export class ClockSync {
  constructor() {
    this.offset = null;
    this.bestRtt = Infinity;
  }
  update(sentAt, receivedAt, serverT) {
    const rtt = receivedAt - sentAt;
    if (!(rtt >= 0) || typeof serverT !== "number") return;
    // Let old estimates age out slowly so a one-off lucky sample doesn't stick forever.
    this.bestRtt *= 1.01;
    if (rtt <= this.bestRtt) {
      this.bestRtt = rtt;
      this.offset = serverT - (sentAt + rtt / 2);
    }
  }
  serverNow(localNow) {
    return this.offset === null ? null : localNow + this.offset;
  }
}

/** Tracks multi-touch pointers for the hold-to-move deadman. */
export class Deadman {
  constructor() {
    this.pointers = new Set();
  }
  down(id) {
    this.pointers.add(id);
    return this.held;
  }
  up(id) {
    this.pointers.delete(id);
    return this.held;
  }
  clear() {
    this.pointers.clear();
    return false;
  }
  get held() {
    return this.pointers.size > 0;
  }
}
