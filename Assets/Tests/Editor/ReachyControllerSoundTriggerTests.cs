using NUnit.Framework;
using ReachyMiniTeleop.Reachy;
using UnityEngine;
using UnityEngine.XR;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class ReachyControllerSoundTriggerTests
    {
        [TestCase(0f, 0.8f, XRNode.LeftHand, ControllerSoundDirection.LeftUp)]
        [TestCase(0f, -0.8f, XRNode.LeftHand, ControllerSoundDirection.LeftDown)]
        [TestCase(-0.8f, 0f, XRNode.LeftHand, ControllerSoundDirection.LeftLeft)]
        [TestCase(0.8f, 0f, XRNode.LeftHand, ControllerSoundDirection.LeftRight)]
        [TestCase(0f, 0.8f, XRNode.RightHand, ControllerSoundDirection.RightUp)]
        [TestCase(0f, -0.8f, XRNode.RightHand, ControllerSoundDirection.RightDown)]
        [TestCase(-0.8f, 0f, XRNode.RightHand, ControllerSoundDirection.RightLeft)]
        [TestCase(0.8f, 0f, XRNode.RightHand, ControllerSoundDirection.RightRight)]
        public void ResolveDirection_MapsControllerAxisToHandDirection(
            float x,
            float y,
            XRNode node,
            ControllerSoundDirection expected)
        {
            var axis = new Vector2(x, y);

            ControllerSoundDirection direction = ReachyControllerSoundTrigger.ResolveDirection(node, axis, 0.7f);

            Assert.AreEqual(expected, direction);
        }

        [Test]
        public void ResolveDirection_ReturnsNoneBelowThreshold()
        {
            ControllerSoundDirection direction = ReachyControllerSoundTrigger.ResolveDirection(
                XRNode.LeftHand,
                new Vector2(0.69f, 0f),
                0.7f);

            Assert.AreEqual(ControllerSoundDirection.None, direction);
        }

        [Test]
        public void ResolveDirection_UsesDominantAxisForDiagonal()
        {
            ControllerSoundDirection vertical = ReachyControllerSoundTrigger.ResolveDirection(
                XRNode.LeftHand,
                new Vector2(0.72f, 0.9f),
                0.7f);
            ControllerSoundDirection horizontal = ReachyControllerSoundTrigger.ResolveDirection(
                XRNode.RightHand,
                new Vector2(-0.95f, 0.75f),
                0.7f);

            Assert.AreEqual(ControllerSoundDirection.LeftUp, vertical);
            Assert.AreEqual(ControllerSoundDirection.RightLeft, horizontal);
        }

        [Test]
        public void IsReset_RequiresBothAxesInDeadzone()
        {
            Assert.IsTrue(ReachyControllerSoundTrigger.IsReset(new Vector2(0.2f, -0.3f), 0.35f));
            Assert.IsFalse(ReachyControllerSoundTrigger.IsReset(new Vector2(0.36f, 0f), 0.35f));
        }

        [Test]
        public void TriggerGate_TriggersOnceWhileHeld()
        {
            var gate = new ReachyControllerSoundTrigger.TriggerGate();

            Assert.IsTrue(gate.Evaluate(isActive: true, isReset: false, now: 1f, cooldownUntil: 0f));
            Assert.IsFalse(gate.Evaluate(isActive: true, isReset: false, now: 2f, cooldownUntil: 0f));
            Assert.IsFalse(gate.IsArmed);
        }

        [Test]
        public void TriggerGate_RearmsAfterAxisReturnsToCenter()
        {
            var gate = new ReachyControllerSoundTrigger.TriggerGate();

            Assert.IsTrue(gate.Evaluate(isActive: true, isReset: false, now: 1f, cooldownUntil: 0f));
            Assert.IsFalse(gate.Evaluate(isActive: false, isReset: true, now: 2f, cooldownUntil: 0f));
            Assert.IsTrue(gate.IsArmed);
            Assert.IsTrue(gate.Evaluate(isActive: true, isReset: false, now: 3f, cooldownUntil: 0f));
        }

        [Test]
        public void TriggerGate_CooldownSuppressesAndDisarmsHeldDirection()
        {
            var gate = new ReachyControllerSoundTrigger.TriggerGate();

            bool triggered = gate.Evaluate(isActive: true, isReset: false, now: 1f, cooldownUntil: 2f);

            Assert.IsFalse(triggered);
            Assert.IsFalse(gate.IsArmed);
        }

        [Test]
        public void DefaultBindings_MapEightControllerDirections()
        {
            ReachyControllerSoundTrigger.ControllerSoundBinding[] bindings =
                ReachyControllerSoundTrigger.ControllerSoundBinding.CreateDefaults();

            Assert.AreEqual(8, bindings.Length);
            Assert.AreEqual(ControllerSoundDirection.LeftUp, bindings[0].direction);
            Assert.AreEqual("wake_up.wav", bindings[0].soundFile);
            Assert.AreEqual(ControllerSoundDirection.LeftDown, bindings[1].direction);
            Assert.AreEqual("go_sleep.wav", bindings[1].soundFile);
            Assert.AreEqual(ControllerSoundDirection.LeftLeft, bindings[2].direction);
            Assert.AreEqual("impatient1.wav", bindings[2].soundFile);
            Assert.AreEqual(ControllerSoundDirection.LeftRight, bindings[3].direction);
            Assert.AreEqual("confused1.wav", bindings[3].soundFile);
            Assert.AreEqual(ControllerSoundDirection.RightUp, bindings[4].direction);
            Assert.AreEqual("count.wav", bindings[4].soundFile);
            Assert.AreEqual(ControllerSoundDirection.RightDown, bindings[5].direction);
            Assert.AreEqual("dance1.wav", bindings[5].soundFile);
            Assert.AreEqual(ControllerSoundDirection.RightLeft, bindings[6].direction);
            Assert.AreEqual("wake_up.wav", bindings[6].soundFile);
            Assert.AreEqual(ControllerSoundDirection.RightRight, bindings[7].direction);
            Assert.AreEqual("go_sleep.wav", bindings[7].soundFile);
        }

        [Test]
        public void DefaultBindings_AllowEmptySoundFileToDisableDirection()
        {
            Assert.IsFalse(ReachyControllerSoundTrigger.IsSoundBindingEnabled(string.Empty));
            Assert.IsFalse(ReachyControllerSoundTrigger.IsSoundBindingEnabled("   "));
            Assert.IsTrue(ReachyControllerSoundTrigger.IsSoundBindingEnabled("wake_up.wav"));
        }

        [Test]
        public void FormatTriggerRequestStatus_IncludesDirectionSoundAndHost()
        {
            string status = ReachyControllerSoundTrigger.FormatTriggerRequestStatus(
                ControllerSoundDirection.LeftUp,
                "wake_up.wav",
                "http://10.0.71.91:8000/api");

            Assert.AreEqual("Controller sound request: LeftUp -> wake_up.wav @ 10.0.71.91:8000", status);
        }

        [Test]
        public void StatusFormatters_UseDiagnosticText()
        {
            Assert.AreEqual("Controller sound: cooldown", ReachyControllerSoundTrigger.FormatCooldownStatus());
            Assert.AreEqual("Controller sound: request pending", ReachyControllerSoundTrigger.FormatRequestPendingStatus());
            Assert.AreEqual("Controller input: no thumbstick data", ReachyControllerSoundTrigger.FormatNoThumbstickDataStatus());
        }
    }
}
