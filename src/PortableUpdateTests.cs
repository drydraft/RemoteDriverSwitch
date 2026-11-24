using System;
using System.IO;

namespace RemoteDriverSwitch
{
    internal static class PortableUpdateTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("portable update atomically swaps a verified fixture and rolls back launch failure", () =>
                WithFixture((target, staged, originalHash, stagedHash) =>
                {
                    bool attempted = false;
                    try {
                        PortableUpdate.ReplaceAndLaunch(target, staged, originalHash, stagedHash, () =>
                        {
                            attempted = true;
                            if (File.ReadAllText(target) != "new") throw new InvalidOperationException("new fixture was not installed");
                            throw new IOException("injected launch failure");
                        });
                        throw new InvalidOperationException("launch error did not propagate");
                    } catch (IOException error) {
                        if (!attempted || error.Message != "injected launch failure" || File.ReadAllText(target) != "old")
                            throw;
                    }
                }));

            test("portable update refuses a staged file that changes after validation", () =>
                WithFixture((target, staged, originalHash, stagedHash) =>
                {
                    File.WriteAllText(staged, "unexpected");
                    try {
                        PortableUpdate.ReplaceAndLaunch(target, staged, originalHash, stagedHash, () =>
                            { throw new InvalidOperationException("unverified fixture was launched"); });
                    } catch (IOException) {
                        if (File.ReadAllText(target) == "old") return;
                        throw;
                    }
                    throw new InvalidOperationException("changed fixture was accepted");
                }));
        }

        private static void WithFixture(Action<string, string, string, string> test)
        {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rds-test-" + Guid.NewGuid().ToString("N")));
            string target = Path.Combine(directory.FullName, "test.exe");
            string staged = Path.Combine(directory.FullName, "new.exe");
            try {
                File.WriteAllText(target, "old");
                File.WriteAllText(staged, "new");
                test(target, staged, PortableUpdate.Digest(target), PortableUpdate.Digest(staged));
            } finally {
                File.Delete(target);
                File.Delete(staged);
                directory.Delete();
            }
        }
    }
}
