using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RemoteDriverSwitch
{
    public sealed class RemoteInstallRoot
    {
        public string ProductId,Path;
    }
    public sealed class RemoteProcessInfo
    {
        public int Id;
        public string ProductId,Name,ExecutablePath,StartTimeUtc,Company,ProductName,OriginalFileName;
    }
    public static class RemoteProcessRules
    {
        private static readonly string[] BroadNames={"windows","system32","syswow64","program files","program files (x86)","users","desktop","downloads","documents","appdata","local","roaming","temp","public"};
        private static readonly string[] MaintenanceNames={"uninstall.exe","uninst.exe","setup.exe","upgrade.exe","updater.exe","cleanup_utils.exe","devcon.exe","driverhelper.exe","microsoftedgewebview2setup.exe","psexec64.exe","openconsole.exe","rg.exe"};
        private static readonly Dictionary<string,string[]> Helpers=new Dictionary<string,string[]> {
            {"uu",new[]{"crashpad_handler.exe","bin\\crashpad_handler.exe","bin\\aria2c.exe","bin\\conpty_bridge.exe","bin\\streamercodecdetector.exe","bin\\uuyc-mux.exe"}},
            {"td",new[]{"codec_checker.exe","coretoolsmgrhelper.exe","crashreport.exe","native\\todesk-mcp.exe"}},
            {"oray",new[]{"awesun_guard\\64\\awesun_guard.exe","awesun_guard\\32\\awesun_guard.exe","flutter\\awesun-mcp-server.exe","flutter\\p2ptelemetry.exe","itmprc\\rdp.exe","itmprc\\ssh.exe"}}
        };
        public static string NormalizeRoot(string path)
        {
            try {
                if(string.IsNullOrWhiteSpace(path)||!System.IO.Path.IsPathRooted(path)||path.StartsWith("\\\\",StringComparison.Ordinal))return null;
                string full=System.IO.Path.GetFullPath(path).TrimEnd('\\','/');
                string drive=(System.IO.Path.GetPathRoot(full)??"").TrimEnd('\\','/');
                if(full.Length==0||string.Equals(full,drive,StringComparison.OrdinalIgnoreCase)||full.IndexOf(':')!=1)return null;
                if(BroadNames.Contains(System.IO.Path.GetFileName(full),StringComparer.OrdinalIgnoreCase))return null;
                string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
                if(windows.Length>0&&(full.Equals(windows,StringComparison.OrdinalIgnoreCase)||full.StartsWith(windows+"\\",StringComparison.OrdinalIgnoreCase)))return null;
                return full;
            }catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(PathTooLongException){return null;}
        }
        public static bool IsUnderRoot(string executable,string root)
        {
            string normalized=NormalizeRoot(root);if(normalized==null||string.IsNullOrWhiteSpace(executable)||!System.IO.Path.IsPathRooted(executable))return false;
            try{return System.IO.Path.GetFullPath(executable).StartsWith(normalized+"\\",StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
        }
        private static bool Contains(string value,string token){return (value??"").IndexOf(token,StringComparison.OrdinalIgnoreCase)>=0;}
        public static bool IsVendorIdentity(string product,string company,string productName)
        {
            if(product=="uu")return (Contains(company,"NetEase")||Contains(company,"网易"))&&(Contains(productName,"UU")||Contains(productName,"GameViewer"));
            if(product=="td")return (Contains(company,"YouQu")||Contains(company,"海南有趣"))&&(Contains(productName,"ToDesk")||Contains(productName,"Zuler")||Contains(productName,"Crash Report"));
            if(product=="oray")return (Contains(company,"Oray")||Contains(company,"贝锐"))&&(Contains(productName,"Sunlogin")||Contains(productName,"AweSun")||Contains(productName,"向日葵"));
            return false;
        }
        public static bool IsKnownHelper(string product,string relativePath)
        {
            string[] names;if(!Helpers.TryGetValue(product??"",out names)||string.IsNullOrEmpty(relativePath))return false;
            return names.Contains(relativePath.Replace('/','\\'),StringComparer.OrdinalIgnoreCase);
        }
        public static bool IsMaintenance(string executable)
        {return MaintenanceNames.Contains(System.IO.Path.GetFileName(executable??""),StringComparer.OrdinalIgnoreCase);}
        public static bool CanClose(RemoteProcessInfo process,IEnumerable<RemoteInstallRoot> roots)
        {
            if(process==null||process.Id<=0||string.IsNullOrEmpty(process.ProductId)||string.IsNullOrEmpty(process.StartTimeUtc)||IsMaintenance(process.ExecutablePath))return false;
            foreach(var root in roots??new RemoteInstallRoot[0]) {
                if(root==null||root.ProductId!=process.ProductId||!IsUnderRoot(process.ExecutablePath,root.Path))continue;
                string normalized=NormalizeRoot(root.Path);string relative=System.IO.Path.GetFullPath(process.ExecutablePath).Substring(normalized.Length+1);
                if(IsVendorIdentity(process.ProductId,process.Company,process.ProductName)||IsKnownHelper(process.ProductId,relative))return true;
            }
            return false;
        }
        public static bool SameIdentity(RemoteProcessInfo expected,RemoteProcessInfo current)
        {
            if(expected==null||current==null||expected.Id!=current.Id||expected.ProductId!=current.ProductId||string.IsNullOrEmpty(expected.StartTimeUtc)||expected.StartTimeUtc!=current.StartTimeUtc)return false;
            try{return string.Equals(System.IO.Path.GetFullPath(expected.ExecutablePath),System.IO.Path.GetFullPath(current.ExecutablePath),StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}catch(NotSupportedException){return false;}
        }
    }
    public sealed class RemoteProcessController
    {
        private readonly WindowsPlatform platform;private readonly Action<string> diagnostic;
        private readonly Dictionary<string,FileVersionInfo> fileInfo=new Dictionary<string,FileVersionInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,string[]> Anchors=new Dictionary<string,string[]> {
            {"uu",new[]{"GameViewer.exe","GameViewerService.exe","GameViewerServer.exe","UURemote.exe","UU远程.exe"}},
            {"td",new[]{"ToDesk.exe","ToDesk_Service.exe","ToDesk_ServiceTray.exe"}},
            {"oray",new[]{"AweSun.exe","OldAweSun.exe","SunloginClient.exe","SunloginClient_Service.exe"}}
        };
        public RemoteProcessController(WindowsPlatform platform,Action<string> diagnostic){this.platform=platform;this.diagnostic=diagnostic;}
        private void Trace(string text){if(diagnostic!=null)try{diagnostic(text);}catch{}}
        private static string NameProduct(string name)
        {
            foreach(var product in Anchors)if(product.Value.Any(n=>string.Equals(Path.GetFileNameWithoutExtension(n),name,StringComparison.OrdinalIgnoreCase)))return product.Key;
            if(name!=null&&name.StartsWith("GameViewer",StringComparison.OrdinalIgnoreCase))return "uu";
            if(name!=null&&name.StartsWith("ToDesk",StringComparison.OrdinalIgnoreCase))return "td";
            if(name!=null&&(name.StartsWith("Sunlogin",StringComparison.OrdinalIgnoreCase)||name.StartsWith("AweSun",StringComparison.OrdinalIgnoreCase)||name.Equals("awesun_guard",StringComparison.OrdinalIgnoreCase)))return "oray";
            return null;
        }
        private FileVersionInfo Version(string path)
        {FileVersionInfo version;if(!fileInfo.TryGetValue(path,out version)){version=FileVersionInfo.GetVersionInfo(path);fileInfo[path]=version;}return version;}
        private void AddRoot(List<RemoteInstallRoot> roots,string product,string candidate)
        {
            string normalized=RemoteProcessRules.NormalizeRoot(candidate);if(normalized==null||!Anchors.ContainsKey(product))return;
            if(roots.Any(r=>r.ProductId==product&&r.Path.Equals(normalized,StringComparison.OrdinalIgnoreCase)))return;
            foreach(string anchor in Anchors[product]) {
                string path=Path.Combine(normalized,anchor);if(!File.Exists(path))continue;
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                var version=Version(path);
                if(!RemoteProcessRules.IsVendorIdentity(product,version.CompanyName,version.ProductName))continue;
                FileStore.CheckDirectoryPath(normalized);
                roots.Add(new RemoteInstallRoot {ProductId=product,Path=normalized});Trace("远控进程目录已核验："+product+" | "+normalized+" | "+anchor);return;
            }
        }
        private void AddCandidate(List<RemoteInstallRoot> roots,string product,string directory)
        {
            AddRoot(roots,product,directory);
            if(!string.IsNullOrEmpty(directory)) {
                string leaf=Path.GetFileName(directory.TrimEnd('\\'));
                if(new[]{"bin","agent","flutter"}.Contains(leaf,StringComparer.OrdinalIgnoreCase))AddRoot(roots,product,Path.GetDirectoryName(directory));
            }
        }
        public List<RemoteInstallRoot> DiscoverRoots()
        {
            var roots=new List<RemoteInstallRoot>();var services=new Dictionary<string,ServiceState>(StringComparer.OrdinalIgnoreCase);
            foreach(string name in Catalog.AppServices.Keys) {
                var state=platform.Service(name);if(state==null)continue;services[name]=state;
                string executable=SoftwareInventory.ExecutablePath(state.ImagePath);
                if(Catalog.AppExeMatches(name,Path.GetFileName(executable)))AddCandidate(roots,Catalog.AppProduct(name),Path.GetDirectoryName(executable));
            }
            var warnings=new List<string>();
            foreach(var software in SoftwareInventory.Read(new List<Device>(),services,warnings))if(software.Installed)AddCandidate(roots,software.Id,software.InstallPath);
            foreach(var process in Process.GetProcesses())using(process) {
                string product=null;
                try {
                    product=NameProduct(process.ProcessName);if(product==null)continue;var identity=ReadIdentity(process);
                    AddCandidate(roots,product,Path.GetDirectoryName(identity.ExecutablePath));
                    if(!roots.Any(r=>r.ProductId==product&&RemoteProcessRules.IsUnderRoot(identity.ExecutablePath,r.Path))) {
                        var version=Version(identity.ExecutablePath);
                        if(RemoteProcessRules.IsVendorIdentity(product,version.CompanyName,version.ProductName))throw new IOException("远控程序位于无法安全界定的目录，请先手动退出："+identity.ExecutablePath+"（PID "+identity.Id+"）。尚未继续处理驱动。");
                    }
                }
                catch(InvalidOperationException){}
                catch(Win32Exception error){if(product!=null&&!Exited(process))throw new IOException("无法读取远控后台进程身份，未继续处理驱动：PID "+process.Id+"；"+error.Message,error);}
            }
            return roots;
        }
        private static bool Exited(Process process){try{return process.HasExited;}catch(InvalidOperationException){return true;}catch(Win32Exception){return false;}}
        private RemoteProcessInfo ReadIdentity(Process process)
        {
            IntPtr handle=OpenProcess(0x1000,false,process.Id);if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            try {return ReadIdentity(handle,process.Id);}finally{CloseHandle(handle);}
        }
        private RemoteProcessInfo ReadIdentity(IntPtr handle,int id)
        {
                var path=new StringBuilder(32768);uint length=(uint)path.Capacity;
                if(!QueryFullProcessImageName(handle,0,path,ref length))throw new Win32Exception(Marshal.GetLastWin32Error());
                long created,ended,kernel,user;if(!GetProcessTimes(handle,out created,out ended,out kernel,out user))throw new Win32Exception(Marshal.GetLastWin32Error());
                return new RemoteProcessInfo {Id=id,Name=Path.GetFileName(path.ToString()),ExecutablePath=path.ToString(),StartTimeUtc=DateTime.FromFileTimeUtc(created).ToString("o")};
        }
        private List<RemoteProcessInfo> Find(List<RemoteInstallRoot> roots)
        {
            var targets=new List<RemoteProcessInfo>();int ownId;using(var own=Process.GetCurrentProcess())ownId=own.Id;
            foreach(var process in Process.GetProcesses())using(process) {
                if(process.Id==ownId)continue;
                string hint=null;
                try {
                    hint=NameProduct(process.ProcessName);var info=ReadIdentity(process);
                    var root=roots.Where(r=>RemoteProcessRules.IsUnderRoot(info.ExecutablePath,r.Path)).OrderByDescending(r=>r.Path.Length).FirstOrDefault();
                    if(root==null) {
                        if(hint!=null) {
                            var outsideVersion=Version(info.ExecutablePath);
                            if(RemoteProcessRules.IsVendorIdentity(hint,outsideVersion.CompanyName,outsideVersion.ProductName))throw new IOException("检测到未覆盖的远控进程，请先手动退出："+info.ExecutablePath+"（PID "+info.Id+"）。尚未继续处理驱动。");
                        }
                        continue;
                    }
                    info.ProductId=root.ProductId;
                    var version=Version(info.ExecutablePath);info.Company=version.CompanyName;info.ProductName=version.ProductName;info.OriginalFileName=version.OriginalFilename;
                    if(!RemoteProcessRules.CanClose(info,roots))throw new IOException((RemoteProcessRules.IsMaintenance(info.ExecutablePath)?"检测到远控安装或驱动维护进程，请等待完成":"检测到远控目录中尚未适配的后台进程，请手动退出")+"："+info.ExecutablePath+"（PID "+info.Id+"）。尚未继续处理驱动。");
                    FileStore.CheckDirectoryPath(Path.GetDirectoryName(info.ExecutablePath));
                    if((File.GetAttributes(info.ExecutablePath)&FileAttributes.ReparsePoint)!=0)throw new IOException("进程文件使用链接，无法核验："+info.ExecutablePath);
                    targets.Add(info);
                }catch(InvalidOperationException){}
                catch(Win32Exception error){if(hint!=null&&!Exited(process))throw new IOException("无法核验远控后台进程：PID "+process.Id+"；"+error.Message,error);}
            }
            return targets;
        }
        public List<RemoteProcessInfo> Inspect()
        {return Find(DiscoverRoots());}
        internal static T InvokeWhileRunning<T>(Process process,Func<T> action)
        {
            try{return action();}
            catch(InvalidOperationException){if(!Exited(process))throw;}
            catch(Win32Exception){if(!Exited(process))throw;}
            return default(T);
        }

        private Process OpenCurrent(RemoteProcessInfo expected,List<RemoteInstallRoot> roots)
        {
            Process process;
            try{process=Process.GetProcessById(expected.Id);}catch(ArgumentException){return null;}
            try {
                var current=InvokeWhileRunning(process,()=>{
                    // Pin this process before reading its identity; Close/Kill reuse that handle.
                    IntPtr pinned=process.Handle;
                    return Exited(process)?null:ReadIdentity(pinned,expected.Id);
                });
                if(current==null){process.Dispose();return null;}
                current.ProductId=expected.ProductId;
                if(!RemoteProcessRules.SameIdentity(expected,current)){Trace("跳过已退出或身份已变化的 PID："+expected.Id);process.Dispose();return null;}
                var version=Version(current.ExecutablePath);current.Company=version.CompanyName;current.ProductName=version.ProductName;current.OriginalFileName=version.OriginalFilename;
                if(!RemoteProcessRules.CanClose(current,roots))throw new IOException("关闭前远控进程身份未通过核验："+current.ExecutablePath);
                return process;
            }catch{process.Dispose();throw;}
        }
        public void Close()
        {
            var roots=DiscoverRoots();var watch=Stopwatch.StartNew();
            for(int pass=0;pass<3;pass++) {
                var targets=Find(roots);if(targets.Count==0){Trace("已确认三款远控的已识别后台进程全部退出。");return;}
                foreach(var target in targets) {
                    if(watch.ElapsedMilliseconds>=20000)throw new IOException("等待远控进程退出超时，尚未继续处理驱动。");
                    using(var process=OpenCurrent(target,roots))if(process!=null) {
                        Trace("请求退出远控进程："+target.ProductId+" | PID "+target.Id+" | "+target.ExecutablePath);
                        InvokeWhileRunning(process,process.CloseMainWindow);
                    }
                }
                Thread.Sleep(350);
                foreach(var target in targets) {
                    int remaining=(int)(20000-watch.ElapsedMilliseconds);if(remaining<=0)throw new IOException("等待远控进程退出超时，尚未继续处理驱动。");
                    using(var process=OpenCurrent(target,roots))if(process!=null) {
                        Trace("结束仍在运行的远控进程："+target.ProductId+" | PID "+target.Id+" | "+target.ExecutablePath);
                        InvokeWhileRunning(process,()=>{
                            process.Kill();
                            if(!process.WaitForExit(Math.Min(1500,remaining)))throw new IOException("远控进程仍未退出："+target.ExecutablePath);
                            return true;
                        });
                    }
                }
                Thread.Sleep(200);
            }
            var remainingProcesses=Find(roots);
            if(remainingProcesses.Count>0)throw new IOException("远控后台进程退出后重新出现，尚未继续处理驱动："+string.Join("、",remainingProcesses.Select(p=>p.Name+" ("+p.Id+")")));
            Trace("已确认三款远控的已识别后台进程全部退出。");
        }
        [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int id);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    }
}
