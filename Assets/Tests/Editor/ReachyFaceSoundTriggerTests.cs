using NUnit.Framework;
using ReachyMiniTeleop.Reachy;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class ReachyFaceSoundTriggerTests
    {
        [Test]
        public void TriggerGate_DoesNotTriggerWhenExpressionsAreInvalid()
        {
            var gate = new ReachyFaceSoundTrigger.TriggerGate();

            bool triggered = gate.Evaluate(
                weight: 1f,
                expressionsValid: false,
                triggerThreshold: 0.75f,
                resetThreshold: 0.45f,
                now: 10f,
                cooldownUntil: 0f);

            Assert.IsFalse(triggered);
            Assert.IsTrue(gate.IsArmed);
        }

        [Test]
        public void TriggerGate_TriggersWhenWeightCrossesThreshold()
        {
            var gate = new ReachyFaceSoundTrigger.TriggerGate();

            bool triggered = gate.Evaluate(
                weight: 0.8f,
                expressionsValid: true,
                triggerThreshold: 0.75f,
                resetThreshold: 0.45f,
                now: 10f,
                cooldownUntil: 0f);

            Assert.IsTrue(triggered);
            Assert.IsFalse(gate.IsArmed);
        }

        [Test]
        public void TriggerGate_DoesNotRepeatWhileHeldAboveThreshold()
        {
            var gate = new ReachyFaceSoundTrigger.TriggerGate();

            Assert.IsTrue(gate.Evaluate(0.8f, true, 0.75f, 0.45f, 10f, 0f));
            Assert.IsFalse(gate.Evaluate(0.9f, true, 0.75f, 0.45f, 11f, 0f));
        }

        [Test]
        public void TriggerGate_RearmsAfterDroppingBelowResetThreshold()
        {
            var gate = new ReachyFaceSoundTrigger.TriggerGate();

            Assert.IsTrue(gate.Evaluate(0.8f, true, 0.75f, 0.45f, 10f, 0f));
            Assert.IsFalse(gate.Evaluate(0.3f, true, 0.75f, 0.45f, 11f, 0f));
            Assert.IsTrue(gate.IsArmed);
            Assert.IsTrue(gate.Evaluate(0.8f, true, 0.75f, 0.45f, 12f, 0f));
        }

        [Test]
        public void TriggerGate_CooldownSuppressesTrigger()
        {
            var gate = new ReachyFaceSoundTrigger.TriggerGate();

            bool triggered = gate.Evaluate(
                weight: 0.8f,
                expressionsValid: true,
                triggerThreshold: 0.75f,
                resetThreshold: 0.45f,
                now: 10f,
                cooldownUntil: 12f);

            Assert.IsFalse(triggered);
            Assert.IsFalse(gate.IsArmed);
        }

        [Test]
        public void FaceSoundBinding_DefaultsContainPlannedMappings()
        {
            ReachyFaceSoundTrigger.FaceSoundBinding[] bindings =
                ReachyFaceSoundTrigger.FaceSoundBinding.CreateDefaults();

            Assert.AreEqual(4, bindings.Length);
            Assert.AreEqual(OVRFaceExpressions.FaceExpression.JawDrop, bindings[0].expression);
            Assert.AreEqual("count.wav", bindings[0].soundFile);
            Assert.AreEqual(OVRFaceExpressions.FaceExpression.LipCornerPullerL, bindings[1].expression);
            Assert.AreEqual("dance1.wav", bindings[1].soundFile);
            Assert.AreEqual(OVRFaceExpressions.FaceExpression.BrowLowererL, bindings[2].expression);
            Assert.AreEqual("confused1.wav", bindings[2].soundFile);
            Assert.AreEqual(OVRFaceExpressions.FaceExpression.TongueOut, bindings[3].expression);
            Assert.AreEqual("impatient1.wav", bindings[3].soundFile);
        }

        [Test]
        public void FormatTriggerStatus_IncludesExpressionWeightAndSound()
        {
            string status = ReachyFaceSoundTrigger.FormatTriggerStatus(
                OVRFaceExpressions.FaceExpression.JawDrop,
                0.823f,
                "count.wav");

            Assert.AreEqual("Face sound: JawDrop 0.82 -> count.wav", status);
        }

        [Test]
        public void FormatTriggerRequestStatus_IncludesExpressionSoundAndHost()
        {
            string status = ReachyFaceSoundTrigger.FormatTriggerRequestStatus(
                OVRFaceExpressions.FaceExpression.BrowLowererL,
                0.883f,
                "confused1.wav",
                "http://10.0.71.91:8000/api");

            Assert.AreEqual(
                "Face sound request: BrowLowererL 0.88 -> confused1.wav @ 10.0.71.91:8000",
                status);
        }

        [Test]
        public void FormatTrackingStatus_IncludesStrongestExpressionWeight()
        {
            string status = ReachyFaceSoundTrigger.FormatTrackingStatus(
                OVRFaceExpressions.FaceExpression.LipCornerPullerL,
                0.412f);

            Assert.AreEqual("Face tracking: LipCornerPullerL 0.41", status);
        }

        [Test]
        public void FormatNoValidExpressionsStatus_UsesDiagnosticText()
        {
            Assert.AreEqual(
                "Face tracking: no valid expressions",
                ReachyFaceSoundTrigger.FormatNoValidExpressionsStatus());
        }

        [Test]
        public void FormatCooldownStatus_UsesDiagnosticText()
        {
            Assert.AreEqual("Face sound: cooldown", ReachyFaceSoundTrigger.FormatCooldownStatus());
        }

        [Test]
        public void FormatRequestPendingStatus_UsesDiagnosticText()
        {
            Assert.AreEqual("Face sound: request pending", ReachyFaceSoundTrigger.FormatRequestPendingStatus());
        }

        [Test]
        public void DefaultGlobalCooldownSeconds_UsesQuestStableValue()
        {
            var trigger = new UnityEngine.GameObject("FaceSoundTriggerTest").AddComponent<ReachyFaceSoundTrigger>();

            Assert.AreEqual(10f, trigger.globalCooldownSeconds);

            UnityEngine.Object.DestroyImmediate(trigger.gameObject);
        }

        [Test]
        public void TriggerGate_RespectsTenSecondCooldownUntilRearmed()
        {
            var gate = new ReachyFaceSoundTrigger.TriggerGate();
            const float cooldownUntil = 20f;

            Assert.IsTrue(gate.Evaluate(0.8f, true, 0.75f, 0.45f, 10f, 0f));
            Assert.IsFalse(gate.Evaluate(0.2f, true, 0.75f, 0.45f, 11f, cooldownUntil));
            Assert.IsTrue(gate.IsArmed);
            Assert.IsFalse(gate.Evaluate(0.8f, true, 0.75f, 0.45f, 19f, cooldownUntil));
            Assert.IsFalse(gate.IsArmed);
            Assert.IsFalse(gate.Evaluate(0.2f, true, 0.75f, 0.45f, 20.5f, cooldownUntil));
            Assert.IsTrue(gate.IsArmed);
            Assert.IsTrue(gate.Evaluate(0.8f, true, 0.75f, 0.45f, 21f, cooldownUntil));
        }
    }
}
