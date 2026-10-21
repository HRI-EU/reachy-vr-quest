import test from "node:test";
import assert from "node:assert/strict";
import { ClockSync, Deadman, RateMeter, chooseProducer, orientMessage } from "../../phone_teleop/web/lib.mjs";

test("RateMeter: steady 60 Hz", () => {
  const m = new RateMeter(2000);
  for (let i = 0; i <= 120; i++) m.tick(i * (1000 / 60));
  const s = m.stats(2000);
  assert.ok(Math.abs(s.hz - 60) < 1e-6, String(s.hz));
  assert.ok(s.jitterMs < 1e-6);
  assert.ok(Math.abs(s.maxMs - 1000 / 60) < 1e-6);
});

test("RateMeter: empty / single sample", () => {
  const m = new RateMeter();
  assert.deepEqual(m.stats(0), { hz: 0, meanMs: 0, jitterMs: 0, maxMs: 0 });
  m.tick(5);
  assert.deepEqual(m.stats(5), { hz: 0, meanMs: 0, jitterMs: 0, maxMs: 0 });
});

test("RateMeter: window drops old samples, gaps measured", () => {
  const m = new RateMeter(1000);
  [0, 10, 20, 1500, 1520, 1600].forEach((t) => m.tick(t));
  const s = m.stats(1600);
  assert.equal(m.times.length, 3);
  assert.equal(s.maxMs, 80);
  assert.ok(Math.abs(s.meanMs - 50) < 1e-9);
  // stats() long after the last tick: everything is outside the window
  assert.equal(m.stats(10000).hz, 0);
});

test("chooseProducer prefers reachymini (case-insensitive)", () => {
  const producers = [
    { id: "a", meta: { name: "other" } },
    { id: "b", meta: { name: "ReachyMini" } },
  ];
  assert.equal(chooseProducer(producers), "b");
  assert.equal(chooseProducer([{ id: "x", meta: null }, { id: "y" }]), "x");
  assert.equal(chooseProducer([]), null);
  assert.equal(chooseProducer(undefined), null);
  assert.equal(chooseProducer("nope"), null);
  assert.equal(chooseProducer([{ meta: { name: "reachymini" } }]), null);
  assert.equal(chooseProducer([{ id: "" }]), null);
  assert.equal(chooseProducer([null, { id: "z" }]), null); // first entry null -> no id
});

test("orientMessage", () => {
  const msg = orientMessage({ alpha: 1, beta: 2, gamma: 3 }, true, 7, 123.4);
  assert.deepEqual(msg, { type: "orient", a: 1, b: 2, g: 3, held: true, seq: 7, t: 123.4, src: "evt" });
  assert.deepEqual(orientMessage({ alpha: 1, beta: 2, gamma: 3, timeStamp: 99.5 }, false, 1, 2, "keepalive"),
    { type: "orient", a: 1, b: 2, g: 3, held: false, seq: 1, t: 2, src: "keepalive", et: 99.5 });
  assert.equal(orientMessage({ alpha: 1, beta: 2, gamma: 3 }, "yes", 0, 0).held, false);
  assert.equal(orientMessage({ alpha: null, beta: 2, gamma: 3 }, true, 0, 0), null);
  assert.equal(orientMessage({ alpha: NaN, beta: 2, gamma: 3 }, true, 0, 0), null);
  assert.equal(orientMessage({ alpha: Infinity, beta: 2, gamma: 3 }, true, 0, 0), null);
  assert.equal(orientMessage({ alpha: "1", beta: 2, gamma: 3 }, true, 0, 0), null);
  assert.equal(orientMessage(null, true, 0, 0), null);
  assert.equal(orientMessage(undefined, true, 0, 0), null);
  // JSON round trip keeps numbers
  assert.deepEqual(JSON.parse(JSON.stringify(msg)), msg);
});

test("Deadman multi-touch", () => {
  const d = new Deadman();
  assert.equal(d.held, false);
  assert.equal(d.down(1), true);
  assert.equal(d.down(2), true);
  assert.equal(d.up(1), true); // still one finger down
  assert.equal(d.up(1), true); // duplicate up (pointerup + lostpointercapture)
  assert.equal(d.up(2), false);
  assert.equal(d.up(99), false);
  d.down(3);
  assert.equal(d.clear(), false);
  assert.equal(d.held, false);
});

test("ClockSync keeps the lowest-RTT estimate", () => {
  const c = new ClockSync();
  assert.equal(c.serverNow(1000), null);
  // true offset +5000 ms; symmetric 10 ms RTT
  c.update(1000, 1010, 1005 + 5000);
  assert.equal(c.serverNow(2000), 7000);
  // worse RTT with asymmetric delay: ignored
  c.update(2000, 2100, 2090 + 5000);
  assert.equal(c.offset, 5000);
  // better RTT wins
  c.update(3000, 3004, 3002 + 5001);
  assert.equal(c.offset, 5001);
  // garbage ignored
  c.update(5000, 4000, 1);
  c.update(5000, 5001, undefined);
  c.update(NaN, 5001, 7);
  assert.equal(c.offset, 5001);
});

test("ClockSync ages old best RTT so it can re-lock", () => {
  const c = new ClockSync();
  c.update(0, 2, 1 + 100);
  for (let i = 0; i < 200; i++) c.update(10 * i, 10 * i + 10, 10 * i + 5 + 200);
  assert.equal(c.offset, 200);
});
