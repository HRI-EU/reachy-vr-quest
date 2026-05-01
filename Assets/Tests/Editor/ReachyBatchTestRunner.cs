#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace ReachyMiniTeleop.Tests.Editor
{
    public static class ReachyBatchTestRunner
    {
        public static void RunEditMode()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
            api.Execute(new ExecutionSettings(new Filter
            {
                testMode = UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode
            }));
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), "ReachyEditModeTestSummary.txt");
                var summary = new StringBuilder();
                summary.AppendLine($"passed={result.PassCount}");
                summary.AppendLine($"failed={result.FailCount}");
                summary.AppendLine($"skipped={result.SkipCount}");
                AppendFailures(result, summary);
                File.WriteAllText(path, summary.ToString());

                EditorApplication.Exit(result.FailCount == 0 ? 0 : 1);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }

            private static void AppendFailures(ITestResultAdaptor result, StringBuilder summary)
            {
                if (result == null)
                    return;

                if (result.FailCount > 0 && !result.HasChildren)
                {
                    summary.AppendLine($"failure={result.FullName}");
                    summary.AppendLine($"message={result.Message}");
                    summary.AppendLine($"stack={result.StackTrace}");
                }

                if (!result.HasChildren)
                    return;

                foreach (var child in result.Children)
                    AppendFailures(child, summary);
            }
        }
    }
}
#endif
