using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RemoteDriverSwitch
{
    public sealed class SoftwareInfo
    {
        public string Id="",Name="",Version="",InstallPath="";
        public bool Installed,ProcessRunning,ServiceDetected,ServiceRunning;
        public List<string> Evidence=new List<string>();
    }

    public sealed class ScanResult
    {
        public List<Device> Devices=new List<Device>();
        public List<SoftwareInfo> Software=new List<SoftwareInfo>();
        public List<StartupEntry> Startups=new List<StartupEntry>();
        public Dictionary<string,ServiceState> Services=new Dictionary<string,ServiceState>(StringComparer.OrdinalIgnoreCase);
        public string OsDescription="",ComputerName="",ScannedAt="",BootId="";
        public List<string> Warnings=new List<string>();

        // Call on a worker thread. This performs no service or device mutations.
        public static ScanResult Capture(IPlatform platform)
        {
            if(platform==null)throw new ArgumentNullException("platform");
            var result=new ScanResult {ComputerName=Environment.MachineName,ScannedAt=DateTimeOffset.Now.ToString("o"),OsDescription=SoftwareInventory.ReadOsDescription()};
            result.Devices=platform.Devices();
            foreach(string name in Catalog.DriverNames.Concat(Catalog.AppServices.Keys).Distinct(StringComparer.OrdinalIgnoreCase)) {
                try {var service=platform.Service(name);if(service!=null)result.Services[name]=service;}
                catch(Exception error){result.Warnings.Add("服务读取失败："+name+"；"+error.Message);}
            }
            result.Software=platform is WindowsPlatform?SoftwareInventory.Read(result.Devices,result.Services,result.Warnings):platform.Software(result.Devices);
            try {result.Startups=platform.StartupEntries();}
            catch(Exception error){result.Warnings.Add("远控登录启动项读取失败："+error.Message);}
            try {result.BootId=platform.BootId;}
            catch(Exception error){result.Warnings.Add("启动时间读取失败："+error.Message);}
            return result;
        }
    }

    internal static class SoftwareInventory
    {
        private const string UninstallPath="SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
        private static readonly Dictionary<string,string> ProcessProducts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"GameViewer","uu"},{"UURemote","uu"},{"UU远程","uu"},
            {"ToDesk","td"},{"ToDesk_ServiceTray","td"},
            {"SunloginClient","oray"},{"AweSun","oray"},{"OldAweSun","oray"}
        };

        internal static List<SoftwareInfo> Read(List<Device> devices,IDictionary<string,ServiceState> services,List<string> warnings)
        {
            var result=new List<SoftwareInfo> {
                new SoftwareInfo {Id="uu",Name="UU远程"},new SoftwareInfo {Id="td",Name="ToDesk"},new SoftwareInfo {Id="oray",Name="向日葵"}
            };
            var products=result.ToDictionary(item=>item.Id,StringComparer.OrdinalIgnoreCase);
            RegistryView[] views=Environment.Is64BitOperatingSystem?new[]{RegistryView.Registry64,RegistryView.Registry32}:new[]{RegistryView.Registry32};
            foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})foreach(var view in views)ReadUninstall(hive,view,products,warnings);

            foreach(var pair in Catalog.AppServices) {
                ServiceState service;
                if(!services.TryGetValue(pair.Key,out service))continue;
                string executable=ExecutablePath(service.ImagePath);
                string product=Catalog.AppProduct(pair.Key);
                var item=products[product];
                if(!Catalog.AppExeMatches(pair.Key,FileName(executable))) {
                    AddEvidence(item,"同名服务待核对："+pair.Key+"（可执行文件不匹配）");
                    continue;
                }
                item.Installed=true;item.ServiceDetected=true;item.ServiceRunning|=service.Running;
                AddEvidence(item,"服务："+pair.Key+"（"+(service.Running?"运行中":"已停止")+"）");
                ApplyExecutable(item,executable);
            }
            foreach(var device in devices??new List<Device>()) {
                string product=Catalog.DeviceProduct(device);
                if(product==null||!products.ContainsKey(product))continue;
                var item=products[product];item.Installed=true;
                bool supported=Catalog.Targets.Any(target=>target.Matches(device));
                AddEvidence(item,(supported?"已适配设备：":"厂商设备线索（需适配）：")+(device.Name??device.InstanceId)+" ["+device.InstanceId+"]");
            }
            ReadProcesses(products,warnings);
            return result;
        }

        private static void ReadUninstall(RegistryHive hive,RegistryView view,IDictionary<string,SoftwareInfo> products,List<string> warnings)
        {
            string source=(hive==RegistryHive.LocalMachine?"HKLM":"HKCU")+(view==RegistryView.Registry64?"64":"32");
            try {
                using(var root=RegistryKey.OpenBaseKey(hive,view))using(var uninstall=root.OpenSubKey(UninstallPath)) {
                    if(uninstall==null)return;
                    foreach(string name in uninstall.GetSubKeyNames()) {
                        try {
                            using(var entry=uninstall.OpenSubKey(name)) {
                                if(entry==null)continue;
                                string display=Convert.ToString(entry.GetValue("DisplayName"));
                                string product=UninstallProduct(display);if(product==null)continue;
                                var item=products[product];item.Installed=true;
                                string version=Convert.ToString(entry.GetValue("DisplayVersion"));
                                if(string.IsNullOrWhiteSpace(item.Version)&&!string.IsNullOrWhiteSpace(version))item.Version=version;
                                string location=CleanPath(Convert.ToString(entry.GetValue("InstallLocation")));
                                if(string.IsNullOrWhiteSpace(item.InstallPath)&&!string.IsNullOrWhiteSpace(location))item.InstallPath=location;
                                if(string.IsNullOrWhiteSpace(item.InstallPath)) {
                                    string executable=ExecutablePath(Convert.ToString(entry.GetValue("DisplayIcon")));
                                    if(string.IsNullOrWhiteSpace(executable))executable=ExecutablePath(Convert.ToString(entry.GetValue("UninstallString")));
                                    string file=FileName(executable);
                                    if(!file.Equals("msiexec.exe",StringComparison.OrdinalIgnoreCase)&&!file.Equals("rundll32.exe",StringComparison.OrdinalIgnoreCase))ApplyExecutable(item,executable);
                                }
                                AddEvidence(item,"安装记录（"+source+"）："+display+(string.IsNullOrWhiteSpace(version)?"":" "+version));
                            }
                        }catch(System.Security.SecurityException){Warn(warnings,"部分安装记录无读取权限："+source);}
                        catch(UnauthorizedAccessException){Warn(warnings,"部分安装记录无读取权限："+source);}
                        catch(IOException){Warn(warnings,"部分安装记录在扫描期间发生变化："+source);}
                    }
                }
            }catch(Exception error){Warn(warnings,"安装记录读取失败："+source+"；"+error.Message);}
        }

        private static string UninstallProduct(string display)
        {
            string value=display??"";
            if(Contains(value,"ToDesk"))return "td";
            if(Contains(value,"向日葵")||Contains(value,"Sunlogin")||Contains(value,"AweSun"))return "oray";
            if(Contains(value,"UU远程")||Contains(value,"UU 远程")||Contains(value,"UURemote")||Contains(value,"UU Remote")||Contains(value,"GameViewer"))return "uu";
            return null;
        }
        private static bool Contains(string value,string token){return value.IndexOf(token,StringComparison.OrdinalIgnoreCase)>=0;}
        private static void ReadProcesses(IDictionary<string,SoftwareInfo> products,List<string> warnings)
        {
            Process[] processes;
            try {processes=Process.GetProcesses();}
            catch(Exception error){Warn(warnings,"进程列表读取失败："+error.Message);return;}
            foreach(var process in processes)using(process) {
                try {
                    string product;
                    if(!ProcessProducts.TryGetValue(process.ProcessName,out product))continue;
                    var item=products[product];item.Installed=true;item.ProcessRunning=true;
                    AddEvidence(item,"进程："+process.ProcessName+".exe");
                    try {if(process.MainModule!=null)ApplyExecutable(item,process.MainModule.FileName);}
                    catch(System.ComponentModel.Win32Exception){AddEvidence(item,"运行路径：进程访问受限");}
                    catch(InvalidOperationException){}
                }catch(InvalidOperationException){}
                catch(System.ComponentModel.Win32Exception){}
                catch(System.Security.SecurityException){}
                catch(NotSupportedException){}
            }
        }
        private static void ApplyExecutable(SoftwareInfo item,string executable)
        {
            if(string.IsNullOrWhiteSpace(executable))return;
            try {
                string path=CleanPath(executable);
                if(!Path.IsPathRooted(path)||path.StartsWith("\\\\",StringComparison.Ordinal))return;
                if(string.IsNullOrWhiteSpace(item.InstallPath))item.InstallPath=Path.GetDirectoryName(path)??"";
                if(string.IsNullOrWhiteSpace(item.Version)&&File.Exists(path)) {
                    var version=FileVersionInfo.GetVersionInfo(path);
                    item.Version=string.IsNullOrWhiteSpace(version.ProductVersion)?version.FileVersion??"":version.ProductVersion;
                }
            }catch(ArgumentException){}
            catch(IOException){}
            catch(UnauthorizedAccessException){}
            catch(System.Security.SecurityException){}
        }
        internal static string ExecutablePath(string raw)
        {
            string value=CleanPath(raw);if(value.Length==0)return "";
            if(value[0]=='"') {
                int end=value.IndexOf('"',1);return end>1?value.Substring(1,end-1):"";
            }
            int exe=value.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
            if(exe<0)return "";
            return value.Substring(0,exe+4).Trim();
        }
        private static string CleanPath(string raw){return Environment.ExpandEnvironmentVariables((raw??"").Trim());}
        private static string FileName(string path){try{return Path.GetFileName(path??"");}catch(ArgumentException){return "";}}
        private static void AddEvidence(SoftwareInfo item,string evidence){if(!item.Evidence.Contains(evidence))item.Evidence.Add(evidence);}
        private static void Warn(List<string> warnings,string text){if(warnings!=null&&!warnings.Contains(text))warnings.Add(text);}

        internal static string ReadOsDescription()
        {
            try {
                using(var key=Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion")) {
                    if(key!=null) {
                        string build=Convert.ToString(key.GetValue("CurrentBuildNumber"));int number;
                        string name=Convert.ToString(key.GetValue("ProductName"));
                        if(int.TryParse(build,out number)&&number>=22000&&name.StartsWith("Windows 10",StringComparison.OrdinalIgnoreCase))name="Windows 11"+name.Substring(10);
                        string release=Convert.ToString(key.GetValue("DisplayVersion",key.GetValue("ReleaseId","")));
                        string revision=Convert.ToString(key.GetValue("UBR",""));
                        return name+" "+release+"（"+build+(revision.Length==0?"":"."+revision)+"，"+(Environment.Is64BitOperatingSystem?"64 位":"32 位")+"）";
                    }
                }
            }catch(System.Security.SecurityException){}
            catch(UnauthorizedAccessException){}
            return Environment.OSVersion.VersionString+(Environment.Is64BitOperatingSystem?" 64 位":" 32 位");
        }
    }
}
