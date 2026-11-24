using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace RemoteDriverSwitch
{
    internal static class PortableUpdate
    {
        internal static string GateName {
            get {
#if UPDATE_FIXTURE
                if (TestDataRoot != null) return "Local\\RdsFixture." + TestDataRoot.GetHashCode().ToString("X8");
#endif
                return "Global\\RemoteDriverSwitch.ApplyUpdate.v1";
            }
        }
        private const string HelperName = "apply.exe";
        private const string StagedName = "new.exe";
        private const string RunName = "run.exe";
        private const string ActiveName = "active";
        private const string PlanName = "plan.json";
        private const string ReadyName = "ready";
        private const string CommitName = "commit";
        private const string FinishName = "finished";
        private const string InstallerName = "installer";
        private const int HandshakeTimeout = 10000;
        private const int ExitTimeout = 20000;
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static string DataRoot {
            get {
#if UPDATE_FIXTURE
                if (TestDataRoot != null) return TestDataRoot;
#endif
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDriverSwitch");
            }
        }
        private static string UpdateRoot {
            get {
#if UPDATE_FIXTURE
                if (TestDataRoot != null) return Path.Combine(TestDataRoot, "update");
#endif
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDriverSwitch.Update");
            }
        }
#if TESTS || UPDATE_FIXTURE
        private static SecurityIdentifier TestUser { get { using (var id=WindowsIdentity.GetCurrent()) return id.User; } }
#endif
#if UPDATE_FIXTURE
        internal static string TestDataRoot;
        internal static Action<Version, string> TestStage;
#endif

        private sealed class Plan
        {
            public string Target, ParentImage, OldHash, NewHash;
            public int ParentId;
            public long ParentStart;
        }

        private sealed class Receipt : IDisposable
        {
            internal string Stage;
            internal string Target;
            internal Process Installer;
            internal FileStream Active;
            public void Dispose() { if (Installer != null) Installer.Dispose(); if (Active != null) Active.Dispose(); }
        }

        internal static string TargetPath(IDisposable value)
        { return value == null ? Application.ExecutablePath : ((Receipt)value).Target; }

        internal static bool IsAvailable(string dataPath, string executable)
        {
            try { ValidateDestination(dataPath, executable); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static void ValidateDestination(string dataPath, string executable)
        {
            if (!SamePath(dataPath, DataRoot))
                throw new InvalidOperationException("当前数据目录不支持自动更新。");
            if (Exists(Path.Combine(dataPath, "resume.json")))
                throw new IOException("请完成重启核验后，再打开原便携版更新。");
            string path = Path.GetFullPath(executable);
            if (path.StartsWith(Path.GetFullPath(dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请打开原便携版更新。");
            if (!Path.IsPathRooted(executable) || path.Length < 3 || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0)
                throw new IOException("请先将便携版移至本地文件夹。");
            CheckOrdinaryFile(path);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new IOException("当前便携版程序是只读文件。");
        }

        internal static void Start(Version version, string dataPath, string executable)
        {
            RequireAdmin();
            ValidateDestination(dataPath, executable);
            using (AcquireDirectories(Path.GetDirectoryName(executable)))
            using (var oldFile = LockFile(executable))
            using (var parent = Process.GetCurrentProcess()) {
                string parentImage = parent.MainModule.FileName;
                string oldHash = Digest(oldFile);
                ValidateParentImage(parentImage, executable, oldHash);
                string transactionId = Guid.NewGuid().ToString("N");
                string root = UpdateRoot;
                string stage = Path.Combine(root, "update-" + transactionId);
                bool handedOff = false;
                bool helperStarted = false;
                using (AcquireDirectories(Path.GetDirectoryName(root))) try {
                    CreatePrivateDirectory(root);
                    SweepStages(root);
                    CreatePrivateDirectory(stage);
                    using (AcquireDirectories(stage)) {
#if UPDATE_FIXTURE
                    if (TestStage != null) TestStage(version, stage);
                    else
#endif
                        UpdateDownload.Stage(version, stage);
                    string staged = Path.Combine(stage, StagedName);
                    MakePrivateFile(staged);
                    string helper = Path.Combine(stage, HelperName);
                    // Every in-app update must preserve --apply-update and --finish-update v1.
                    File.Copy(staged, helper, false);
                    MakePrivateFile(helper);
                    var plan = new Plan {
                        Target = Path.GetFullPath(executable), ParentId = parent.Id, ParentStart = parent.StartTime.ToUniversalTime().Ticks,
                        ParentImage = parentImage, OldHash = oldHash, NewHash = Digest(staged)
                    };
                    string planPath = Path.Combine(stage, PlanName);
                    File.WriteAllText(planPath, new JavaScriptSerializer().Serialize(plan), new UTF8Encoding(false));
                    MakePrivateFile(planPath);
                    using (LockVerified(helper, plan.NewHash))
                    using (var child = StartImage(helper, "--apply-update " + transactionId)) {
                        helperStarted = true;
                        if (WaitForMarker(stage, ReadyName, child)) {
                            WritePrivate(Path.Combine(stage, CommitName), "");
                            handedOff = true;
                            return;
                        }
                        if (child.HasExited) helperStarted = false;
                        throw new IOException("更新程序未能接管本次更新，当前版本仍可使用。");
                    }
                    }
                } finally {
                    if (!handedOff && !helperStarted) CleanStage(stage);
                }
            }
        }

        internal static int Apply(string[] args)
        {
            Plan plan = null;
            string stage = null;
            Mutex gate = null;
            bool acknowledged = false;
            bool completed = false;
            IDisposable stageDirectories = null;
            try {
                RequireAdmin();
                string transactionId = ReadId(args);
                string root = UpdateRoot;
                stage = Path.Combine(root, "update-" + transactionId);
                if (!SamePath(Application.ExecutablePath, Path.Combine(stage, HelperName)))
                    throw new IOException("更新程序的位置无效。");
                stageDirectories = AcquireDirectories(stage);
                VerifyDirectory(root);
                VerifyDirectory(stage);
                VerifyPrivateFile(Application.ExecutablePath);
                plan = ReadPlan(stage);
                CheckDigest(Application.ExecutablePath, plan.NewHash);
                string staged = Path.Combine(stage, StagedName);
                VerifyPrivateFile(staged);
                CheckDigest(staged, plan.NewHash);
                string run = Path.Combine(stage, RunName);
                File.Copy(staged, run, false);
                MakePrivateFile(run);
                bool created;
                gate = new Mutex(false, GateName, out created);
                if (!created) throw new IOException("另一个更新正在进行。");
                using (AcquireDirectories(Path.GetDirectoryName(plan.Target))) {
                    ValidateDestination(DataRoot, plan.Target);
                    using (var parent = Process.GetProcessById(plan.ParentId))
                    using (LockVerified(plan.Target, plan.OldHash)) {
                        if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStart || !SamePath(parent.MainModule.FileName, plan.ParentImage))
                            throw new IOException("无法确认发起更新的程序。");
                        ValidateParentImage(plan.ParentImage, plan.Target, plan.OldHash);
                        using (var installer = Process.GetCurrentProcess())
                            WritePrivate(Path.Combine(stage, InstallerName), installer.Id + "|" + installer.StartTime.ToUniversalTime().Ticks);
                        WritePrivate(Path.Combine(stage, ReadyName), "");
                        if (!WaitForMarker(stage, CommitName, parent)) return 1;
                        acknowledged = true;
                        if (!parent.WaitForExit(ExitTimeout))
                            throw new IOException("原程序仍在运行，替换未开始。");
                    }
                    ValidateDestination(DataRoot, plan.Target);
                    ReplaceAndLaunch(plan.Target, staged, plan.OldHash, plan.NewHash, () => {
                        using (LockVerified(run, plan.NewHash))
                        using (var replacement = StartImage(run, "--finish-update " + transactionId)) {
                            if (WaitForMarker(stage, FinishName, replacement)) return;
                            if (!replacement.HasExited) {
                                replacement.Kill();
                                if (!replacement.WaitForExit(ExitTimeout))
                                    throw new IOException("新版程序未退出，无法恢复旧版。");
                            }
                            throw new IOException("新版没有完成启动，已停止更新。");
                        }
                    });
                }
                completed = true;
                return 0;
            } catch (Exception error) {
                if (gate != null) { gate.Dispose(); gate = null; }
                if (acknowledged) {
#if UPDATE_FIXTURE
                    ReportError(error.ToString());
#else
                    ReportError(error.Message + " 请重新打开原位置的便携版。");
#endif
                }
                return 1;
            } finally {
                if (gate != null) gate.Dispose();
                if (stageDirectories != null) stageDirectories.Dispose();
                if (acknowledged && !completed) CleanStage(stage, true);
            }
        }

        internal static bool Resume(string[] args, out IDisposable receipt)
        {
            receipt = null;
            RequireAdmin();
            string stage = Path.Combine(UpdateRoot, "update-" + ReadId(args));
            using (AcquireDirectories(stage)) {
                VerifyDirectory(UpdateRoot);
                VerifyDirectory(stage);
                var plan = ReadPlan(stage);
                if (!SamePath(Application.ExecutablePath, Path.Combine(stage, RunName))) return false;
                VerifyPrivateFile(Application.ExecutablePath);
                CheckDigest(Application.ExecutablePath, plan.NewHash);
                using (AcquireDirectories(Path.GetDirectoryName(plan.Target))) {
                    ValidateDestination(DataRoot, plan.Target);
                    CheckDigest(plan.Target, plan.NewHash);
                }
                Mutex gate;
                if (!Mutex.TryOpenExisting(GateName, out gate)) return false;
                using (gate) {
                    string installerPath = Path.Combine(stage, InstallerName);
                    VerifyPrivateFile(installerPath);
                    var identity = File.ReadAllText(installerPath).Split('|');
                    int pid;
                    long ticks;
                    if (identity.Length != 2 || !int.TryParse(identity[0], out pid) || !long.TryParse(identity[1], out ticks))
                        return false;
                    var process = Process.GetProcessById(pid);
                    try {
                        if (process.StartTime.ToUniversalTime().Ticks != ticks || !SamePath(process.MainModule.FileName, Path.Combine(stage, HelperName)))
                            return false;
                        var active = new FileStream(Path.Combine(stage, ActiveName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                        receipt = new Receipt { Stage = stage, Target = plan.Target, Installer = process, Active = active };
                        return true;
                    } finally {
                        if (receipt == null) process.Dispose();
                    }
                }
            }
        }

        internal static void Ready(IDisposable value)
        {
            var receipt = (Receipt)value;
            WritePrivate(Path.Combine(receipt.Stage, FinishName), "");
            var process = receipt.Installer;
            receipt.Installer = null;
            ThreadPool.QueueUserWorkItem(_ => {
                using (process) {
                    try { if (process.WaitForExit(ExitTimeout)) CleanStage(receipt.Stage, false, true); }
                    catch (InvalidOperationException) { }
                }
            });
        }

        private static string ReadId(string[] args)
        {
            Guid id;
            if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out id))
                throw new IOException("无效的更新请求。");
            return id.ToString("N");
        }

        private static Plan ReadPlan(string stage)
        {
            string path = Path.Combine(stage, PlanName);
            VerifyPrivateFile(path);
            if (new FileInfo(path).Length > 32768) throw new IOException("更新请求过大。");
            var plan = new JavaScriptSerializer { MaxJsonLength = 32768, RecursionLimit = 8 }.Deserialize<Plan>(File.ReadAllText(path));
            if (plan == null || plan.ParentId <= 0 || plan.ParentStart <= 0 || string.IsNullOrEmpty(plan.ParentImage) ||
                string.IsNullOrEmpty(plan.Target) || string.IsNullOrEmpty(plan.OldHash) || string.IsNullOrEmpty(plan.NewHash))
                throw new IOException("更新请求不完整。");
            return plan;
        }

        private static bool WaitForMarker(string stage, string name, Process process)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < HandshakeTimeout) {
                if (Exists(Path.Combine(stage, name))) return true;
                if (process.HasExited) break;
                Thread.Sleep(50);
            }
            return Exists(Path.Combine(stage, name));
        }

        private static void ReportError(string message)
        {
#if UPDATE_FIXTURE
            if (TestDataRoot != null) { File.AppendAllText(Path.Combine(TestDataRoot, "update-error.txt"), message); return; }
#endif
            MessageBox.Show(message, "更新未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static void ValidateParentImage(string image, string target, string hash)
        {
            if (SamePath(image, target)) return;
            string directory = Path.GetDirectoryName(image);
            if (!SamePath(Path.GetDirectoryName(directory), UpdateRoot) ||
                !string.Equals(Path.GetFileName(image), RunName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法确认当前程序的位置。");
            using (AcquireDirectories(directory)) {
                VerifyDirectory(UpdateRoot);
                VerifyDirectory(directory);
                VerifyPrivateFile(image);
                CheckDigest(image, hash);
            }
        }

        internal static void ReplaceAndLaunch(string target, string staged, string oldHash, string newHash, Action launch)
        {
            string directory = Path.GetDirectoryName(target);
            string unique = Path.Combine(directory, ".RemoteDriverSwitch-" + Guid.NewGuid().ToString("N"));
            string replacement = Path.Combine(unique, "new.exe");
            string backup = Path.Combine(unique, "old.exe");
            using (AcquireDirectories(directory)) {
                CreatePrivateDirectory(unique);
                bool installed = false;
                bool keepBackup = false;
                try {
                using (AcquireDirectories(unique)) {
                    VerifyDirectory(unique);
                    try {
                        using (var source = LockVerified(staged, newHash))
                        using (var local = new FileStream(replacement, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                            source.CopyTo(local);
                            local.Flush(true);
                        }
                        CheckDigest(replacement, newHash);
                        CheckDigest(target, oldHash);
                        try {
                            File.Replace(replacement, target, backup);
                            installed = true;
                            CheckDigest(backup, oldHash);
                            using (LockVerified(target, newHash)) launch();
                        } catch {
                            try {
                                if (Exists(backup)) {
                                    CheckDigest(backup, oldHash);
                                    if (Exists(target)) File.Replace(backup, target, null);
                                    else File.Move(backup, target);
                                    CheckDigest(target, oldHash);
                                } else if (installed || !Exists(target)) {
                                    throw new IOException("找不到上一版本的备份。");
                                }
                            } catch (Exception rollback) {
                                keepBackup = true;
                                throw new IOException("自动恢复旧版未完成："+rollback.Message+"。旧版备份位置："+backup, rollback);
                            }
                            throw;
                        }
                    } finally {
                        TryDelete(replacement);
                        if (!keepBackup) TryDelete(backup);
                    }
                }
                } finally {
                    if (!keepBackup) TryDeleteDirectory(unique);
                }
            }
        }

        private static void RequireAdmin()
        {
#if UPDATE_FIXTURE
            if (TestDataRoot != null) return;
#endif
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("更新需要管理员权限。");
        }

        private static Process StartImage(string image, string arguments)
        {
            var process = Process.Start(new ProcessStartInfo {
                FileName = image, Arguments = arguments, UseShellExecute = false,
                WorkingDirectory = Environment.SystemDirectory, CreateNoWindow = true
            });
            if (process == null) throw new IOException("无法启动更新程序。");
            return process;
        }

        private static void CreatePrivateDirectory(string path)
        {
            var security = new DirectorySecurity();
            security.SetOwner(Owner);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in TrustedPrincipals())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path, security);
            VerifyDirectory(path);
        }

        private static void MakePrivateFile(string path)
        {
            CheckOrdinaryFile(path);
            var security = new FileSecurity();
            security.SetOwner(Owner);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in TrustedPrincipals())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(path, security);
            VerifyPrivateFile(path);
        }

        private static void VerifyDirectory(string path)
        {
            FileStore.CheckDirectoryPath(path);
            VerifyAccess(Directory.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner));
        }

        private static void VerifyPrivateFile(string path)
        {
            CheckOrdinaryFile(path);
            VerifyAccess(File.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner));
        }

        private static void VerifyAccess(FileSystemSecurity security)
        {
            var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
            var principals = TrustedPrincipals();
            if (!principals.Contains(owner))
                throw new UnauthorizedAccessException("更新目录或文件的所有者未经验证。");
            if (!security.AreAccessRulesProtected ||
                security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                        !principals.Contains(rule.IdentityReference)))
                throw new UnauthorizedAccessException("更新目录或文件的权限未经验证。");
        }

        private static SecurityIdentifier[] TrustedPrincipals()
        {
#if TESTS || UPDATE_FIXTURE
            return new[] { Admins, SystemAccount, TestUser };
#else
            return new[] { Admins, SystemAccount };
#endif
        }

        private static SecurityIdentifier Owner {
            get {
#if TESTS || UPDATE_FIXTURE
                return TestUser;
#else
                return Admins;
#endif
            }
        }

        private static void WritePrivate(string path, string payload)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(payload);
            MakePrivateFile(path);
        }

        private static void CheckOrdinaryFile(string path)
        {
            FileStore.CheckDirectoryPath(Path.GetDirectoryName(path));
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("更新文件不能是目录或链接。");
        }

        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static bool SamePath(string left, string right)
        { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }

        private static void CheckDigest(string path, string expected)
        { using (LockVerified(path, expected)) { } }

        internal static string Digest(string path)
        {
            using (var stream = LockFile(path))
                return Digest(stream);
        }

        private static string Digest(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static FileStream LockFile(string path, FileShare share = FileShare.Read)
        {
            var handle = OpenHandle(path, 0x80000000, share, false);
            try { return new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }

        private static FileStream LockVerified(string path, string expected, FileShare share = FileShare.Read)
        {
            var file = LockFile(path, share);
            try {
                if (!string.Equals(Digest(file), expected, StringComparison.Ordinal))
                    throw new IOException("更新文件在校验后发生变化。");
                file.Position = 0;
                return file;
            } catch { file.Dispose(); throw; }
        }

        private static void TryDelete(string path)
        { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        private static void TryDeleteDirectory(string path)
        { try { Directory.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        private static void CleanStage(string stage, bool helperRunning = false, bool preserveRunner = false)
        {
            if (stage == null || !Directory.Exists(stage)) return;
            try {
                using (AcquireDirectories(stage)) {
                    VerifyDirectory(stage);
                    if (!preserveRunner) {
                        TryDelete(Path.Combine(stage, ActiveName));
                        if (Exists(Path.Combine(stage, ActiveName))) return;
                        TryDelete(Path.Combine(stage, RunName));
                    }
                    foreach (var file in new[] { ReadyName, CommitName, FinishName, InstallerName, PlanName, StagedName, "release.zip" })
                        TryDelete(Path.Combine(stage, file));
                    if (!helperRunning) TryDelete(Path.Combine(stage, HelperName));
                }
                TryDeleteDirectory(stage);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static void SweepStages(string root)
        {
            using (AcquireDirectories(root))
                foreach (var directory in Directory.EnumerateDirectories(root, "update-*")) {
                    var name = Path.GetFileName(directory);
                    Guid parsed;
                    if (name.Length == 39 && Guid.TryParseExact(name.Substring(7), "N", out parsed) &&
                        Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
                        CleanStage(directory);
                }
        }

        private sealed class DirectoryLease : IDisposable
        {
            internal readonly System.Collections.Generic.List<SafeFileHandle> Handles = new System.Collections.Generic.List<SafeFileHandle>();
            public void Dispose() { for (int i = Handles.Count - 1; i >= 0; --i) Handles[i].Dispose(); }
        }

        private static IDisposable AcquireDirectories(string path)
        {
            var lease = new DirectoryLease();
            try {
                var parts = new System.Collections.Generic.Stack<string>();
                for (var dir = new DirectoryInfo(path); dir != null && dir.Parent != null; dir = dir.Parent)
                    parts.Push(dir.FullName);
                while (parts.Count != 0) lease.Handles.Add(OpenHandle(parts.Pop(), 0x81, FileShare.ReadWrite, true));
                return lease;
            } catch { lease.Dispose(); throw; }
        }

        private static SafeFileHandle OpenHandle(string path, uint access, FileShare share, bool directory)
        {
            // Open the entry itself, then check its attributes without resolving links.
            const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
            var handle = CreateFile(path, access, share, IntPtr.Zero, 3, OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);
            if (handle.IsInvalid) {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("无法锁定更新路径：" + path, new Win32Exception(error));
            }
            try {
                FileAttributeTag info;
                if (!GetFileInformationByHandleEx(handle, 9, out info, (uint)Marshal.SizeOf(typeof(FileAttributeTag))))
                    throw new IOException("无法验证更新路径：" + path, new Win32Exception(Marshal.GetLastWin32Error()));
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    ((info.Attributes & FileAttributes.Directory) != 0) != directory)
                    throw new IOException("更新路径不能使用链接或重解析点：" + path);
                return handle;
            } catch { handle.Dispose(); throw; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTag { internal FileAttributes Attributes; internal uint Tag; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTag information, uint size);
    }
}
