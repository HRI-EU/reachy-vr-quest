using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class ReachyAndroidManifestTests
    {
        [Test]
        public void AndroidManifest_AllowsQuestToCallReachyLiteDaemonOverHttp()
        {
            string manifestPath = Path.Combine(
                Application.dataPath,
                "Plugins",
                "Android",
                "AndroidManifest.xml");

            string manifest = File.ReadAllText(manifestPath);

            StringAssert.Contains(
                "android.permission.INTERNET",
                manifest,
                "Quest builds need INTERNET permission to call the Reachy Lite daemon on the PC.");
            StringAssert.Contains(
                "android:usesCleartextTraffic=\"true\"",
                manifest,
                "Quest builds need cleartext HTTP enabled for http://<pc-host>:8000/api.");
        }
    }
}
