using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteDriverSwitch
{
    public sealed class Target
    {
        public string Id, Name, Function, HardwareId, DriverService, DeviceService, Provider;
        public int Order;
        public string[] DeviceServiceAliases=new string[0], ExtraDriverServices=new string[0];
        public string ProductId {get{return Id.StartsWith("uu-")?"uu":Id.StartsWith("oray-")?"oray":"td";}}
        public IEnumerable<string> DriverServices {get{return (DriverService==null?new string[0]:new[]{DriverService}).Concat(ExtraDriverServices);}}
        public Target(string id, string name, string function, string hardwareId, string driverService, string deviceService, string provider, int order)
        { Id=id; Name=name; Function=function; HardwareId=hardwareId; DriverService=driverService; DeviceService=deviceService; Provider=provider; Order=order; }
        public bool Matches(Device d)
        {
            return d != null && d.HardwareIds.Any(h=>string.Equals(h, HardwareId, StringComparison.OrdinalIgnoreCase))
                && new[]{DeviceService}.Concat(DeviceServiceAliases).Contains(d.Service,StringComparer.OrdinalIgnoreCase)
                && Catalog.ProviderMatches(ProductId,Provider,d.Provider);
        }
    }
    public static class Catalog
    {
        public static readonly Target[] Targets = {
            new Target("uu-hid","UU远程 · 虚拟键鼠","虚拟 HID 输入","netease\\uuycinput","uuycinput","uuycinput","Netease",20),
            new Target("uu-filter","UU远程 · 鼠标过滤器","虚拟鼠标过滤","HID\\VID_5E2A&PID_71B4&Col03","uuycinputmf","mouhid","Netease",10),
            new Target("uu-display","UU远程 · 虚拟显示","虚拟显示器","Root\\GameViewerIddDriver",null,"WUDFRd","GameViewer",30),
            new Target("oray-usb","向日葵 · 虚拟 USB","虚拟 USB 控制器","root\\OrayUSBVHCI","OrayUSBVHCI","OrayUSBVHCI","Shanghai Best Oray Information Technology Co., Ltd.",30),
            new Target("oray-gamepad","向日葵 · 虚拟手柄","虚拟游戏控制器","oray\\orayvgc","OrayVGC","OrayVGC","Oray Technologies, Inc.",30),
            new Target("oray-display","向日葵 · 虚拟显示","虚拟显示器","Root\\OrayIddDriver",null,"WUDFRd","Shanghai Best Oray Information Technology Co., Ltd.",30),
            new Target("td-audio","ToDesk · 虚拟音频","虚拟扬声器 / 麦克风","Root\\ToDeskAudio","ToDeskAudio","ToDeskAudio","Hainan YouQu Technology Co. Ltd(R)",30),
            new Target("td-vhid","ToDesk · 虚拟键鼠","虚拟 HID 输入","root\\TodeskVhid",null,"mshidumdf","Hainan YouQu Info Tech",20){DeviceServiceAliases=new[]{"WUDFRd"}},
            new Target("td-vhidex","ToDesk · 增强虚拟键鼠","虚拟 HID 输入","root\\ToDeskVhidEx",null,"mshidumdf","Hainan YouQu Technology Co. Ltd(R)",20){DeviceServiceAliases=new[]{"WUDFRd"}},
            new Target("td-filter","ToDesk · 鼠标增强","虚拟鼠标过滤","HID\\ToDeskVhidEx&Col04","ToDeskVhidEnhance","mouhid","Hainan YouQu Technology Co. Ltd(R)",10),
            new Target("oray-hid","向日葵 · 虚拟键鼠","虚拟 HID 输入","oray\\OrayVHID","orayvhid","orayvhid","Shanghai Best Oray Information Technology Co., Ltd.",20){ExtraDriverServices=new[]{"orayvhidkmdf"}},
            new Target("td-gamepad","ToDesk · 虚拟手柄","虚拟游戏控制器","ToDesk\\TdGamePad\\Gen1","TdGamepad","TdGamepad","Hainan YouQu Info Tech",30),
            new Target("td-display","ToDesk · 虚拟显示","虚拟显示器","Root\\tdIdd",null,"WUDFRd","Hainan YouQu Info Tech",30),
            new Target("td-usbroot","ToDesk · 虚拟 USB 总线","虚拟 USB 总线","ToDeskUsbR\\rootbus","ToDeskVhci","ToDeskVhci","Hainan YouQu Technology Co. Ltd(R)",40),
            new Target("td-vhci","ToDesk · 虚拟 USB 控制器","虚拟 USB 控制器","PCI\\VEN_BADA&DEV_BA42&SUBSYS_AAA1BB42&REV_66","ToDeskVhci","ToDeskVhci","Hainan YouQu Technology Co. Ltd(R)",30),
            new Target("td-vhub","ToDesk · 虚拟 USB 集线器","虚拟 USB 集线器","USB\\ROOT_HUB&VID_BADA&PID_BA43&REV_8843","ToDeskVhci","ToDeskVhci","Hainan YouQu Technology Co. Ltd(R)",10)
        };
        public static readonly Dictionary<string,string> AppServices = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"GameViewerService","GameViewerService.exe"},{"AweSunService","AweSun.exe"},
            {"SunloginService","OldAweSun.exe"},{"ToDesk_Service","ToDesk.exe"}
        };
        public static Target Find(string id) { return Targets.SingleOrDefault(t=>t.Id==id); }
        public static IEnumerable<string> DriverNames {get{return Targets.SelectMany(t=>t.DriverServices).Distinct(StringComparer.OrdinalIgnoreCase);}}
        public static string AppProduct(string name){return name=="GameViewerService"?"uu":name=="ToDesk_Service"?"td":"oray";}
        public static bool AppExeMatches(string name,string exe)
        {
            string expected;if(!AppServices.TryGetValue(name,out expected))return false;
            if(string.Equals(expected,exe,StringComparison.OrdinalIgnoreCase))return true;
            return name=="SunloginService"&&new[]{"SunloginClient.exe","AweSun.exe","SunloginClient_Service.exe"}.Contains(exe,StringComparer.OrdinalIgnoreCase);
        }
        public static bool ProviderMatches(string product,string expected,string actual)
        {
            if(string.Equals(expected,actual,StringComparison.OrdinalIgnoreCase))return true;
            if(product=="td")return new[]{"Hainan YouQu Info Tech","Hainan YouQu Technology Co. Ltd(R)","Hainan YouQu Technology Co., Ltd","Hainan YouQu Technology Co.,Ltd"}.Contains(actual,StringComparer.OrdinalIgnoreCase);
            return false;
        }
        public static string DeviceProduct(Device d)
        {
            var exact=Targets.FirstOrDefault(t=>t.Matches(d));if(exact!=null)return exact.ProductId;
            string p=d.Provider??"";
            if(p.IndexOf("Oray",StringComparison.OrdinalIgnoreCase)>=0)return "oray";
            if(p.IndexOf("YouQu",StringComparison.OrdinalIgnoreCase)>=0)return "td";
            if(p.Equals("Netease",StringComparison.OrdinalIgnoreCase)||p.Equals("GameViewer",StringComparison.OrdinalIgnoreCase))return "uu";
            return null;
        }
    }
    public sealed class Device
    {
        public string InstanceId, Name, Service, Provider, Inf, DriverVersion, ParentId;
        public string[] HardwareIds = new string[0];
        public bool Disabled, Started;
        public uint Problem,RawStatus;
        public int ConfigFlags;
        public bool RebootRequired;
    }
    public sealed class ServiceState
    {
        public string Name, ImagePath;
        public int Start;
        public bool Running;
    }
    public sealed class DeviceRecord
    {
        public string TargetId, InstanceId, ParentId, CurrentInstanceId;
        public bool OriginallyDisabled,OriginallyStarted,DriverOnly;
    }
    public sealed class Snapshot
    {
        public int Version=1;
        public string MachineId, CreatedUtc, AppliedBoot, Phase;
        public List<DeviceRecord> Devices = new List<DeviceRecord>();
        public List<ServiceState> Drivers = new List<ServiceState>();
        public List<ServiceState> Apps = new List<ServiceState>();
        public List<StartupEntry> Startups = new List<StartupEntry>();
        public List<string> PendingDevices = new List<string>();
    }
    public interface IPlatform
    {
        void EnsureReady();
        string MachineId { get; }
        string BootId { get; }
        List<Device> Devices();
        List<SoftwareInfo> Software(List<Device> devices);
        ServiceState Service(string name);
        void SetStart(string name, int value);
        void StopApp(string name);
        void StartApp(string name);
        void CloseRemoteProcesses();
        List<StartupEntry> StartupEntries();
        StartupEntry ReadStartup(StartupEntry entry);
        void SetStartup(StartupEntry entry,bool enabled);
        void SetDevice(Device device, bool enabled);
        void Reboot();
    }
    public interface IStore { Snapshot Load(); void Save(Snapshot snapshot); void Log(string text); }
    public sealed class FileStore : IStore
    {
        public readonly string Root;
        public FileStore(string root, bool secure)
        {
            Root=root;
            if(secure) CheckDirectoryPath(root);
            Directory.CreateDirectory(root);
            if(secure) SecureDirectory(root);
        }
        internal static void CheckDirectoryPath(string path)
        {
            for(var directory=new DirectoryInfo(Path.GetFullPath(path));directory!=null;directory=directory.Parent)
                if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("备份目录不能使用链接或重解析点："+directory.FullName);
        }
        internal static void SecureDirectory(string path)
        {
            CheckDirectoryPath(path);
            var acl = new System.Security.AccessControl.DirectorySecurity();
            acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid,System.Security.Principal.WellKnownSidType.LocalSystemSid})
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid,null),System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.InheritanceFlags.ContainerInherit|System.Security.AccessControl.InheritanceFlags.ObjectInherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        private string StateFile { get { return Path.Combine(Root,"state.json"); } }
        public Snapshot Load()
        {
            if(!File.Exists(StateFile))return null;
            var json=new FileInfo(StateFile);
            if(json.Length>1024*1024)throw new InvalidDataException("状态备份过大，已停止读取。");
            return new JavaScriptSerializer().Deserialize<Snapshot>(File.ReadAllText(StateFile,Encoding.UTF8));
        }
        public void Save(Snapshot s)
        {
            string temp=StateFile+".tmp";
            byte[] data=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(s));
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { stream.Write(data,0,data.Length); stream.Flush(true); }
            if(File.Exists(StateFile))File.Replace(temp,StateFile,StateFile+".bak",true); else File.Move(temp,StateFile);
        }
        public void Log(string text)
        {
            string line=DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")+"  "+text+Environment.NewLine;
            File.AppendAllText(Path.Combine(Root,"actions.log"),line,new UTF8Encoding(false));
        }
    }
    public sealed class SwitchEngine
    {
        private readonly IPlatform platform;
        private readonly IStore store;
        public Action<string> Progress;
        public SwitchEngine(IPlatform platform,IStore store) { this.platform=platform;this.store=store; }
        private void Note(string text) { try{store.Log(text);}catch{} if(Progress!=null)try{Progress(text);}catch{} }
        public Snapshot Current()
        {
            var s=store.Load();if(s!=null)Validate(s);return s;
        }
        public static string ActiveId(DeviceRecord record){return string.IsNullOrEmpty(record.CurrentInstanceId)?record.InstanceId:record.CurrentInstanceId;}
        public List<string> ConfigurationDrift(Snapshot s)
        {
            return ConfigurationDrift(s,ScanResult.Capture(platform));
        }
        public bool CanProtect(Snapshot s,ScanResult scan)
        {
            if(scan==null||string.IsNullOrEmpty(scan.BootId))return false;
            if(s==null)return true;
            Validate(s);
            if(s.PendingDevices.Count>0)return false;
            if(s.Phase!="Protected"&&s.Phase!="Restored")return false;
            if(s.Phase!="Restored"||s.AppliedBoot!=scan.BootId)return true;
            if(scan.Warnings.Count>0||string.IsNullOrEmpty(scan.BootId)||ConfigurationDrift(s,scan).Count>0)return false;
            foreach(var original in s.Drivers.Concat(s.Apps)) {
                ServiceState current;if(!scan.Services.TryGetValue(original.Name,out current)||current.Running!=original.Running)return false;
            }
            foreach(var record in s.Devices) {
                var current=scan.Devices.FirstOrDefault(d=>string.Equals(d.InstanceId,ActiveId(record),StringComparison.OrdinalIgnoreCase));
                if(current==null){if(!record.OriginallyDisabled)return false;continue;}
                if(!AliasParentMatches(record,current,scan.Devices,s))return false;
                if(current.RebootRequired||current.Disabled!=record.OriginallyDisabled||current.Started!=record.OriginallyStarted)return false;
            }
            return true;
        }
        public List<string> ConfigurationDrift(Snapshot s,ScanResult scan)
        {
            Validate(s);var drift=new List<string>();var current=scan.Devices;bool protecting=s.Phase=="Protected";bool rebooted=s.AppliedBoot!=scan.BootId;
            foreach(var original in s.Drivers.Concat(s.Apps)) {
                ServiceState service;scan.Services.TryGetValue(original.Name,out service);int wanted=protecting?4:original.Start;
                if(service==null||service.Start!=wanted||!string.Equals(service.ImagePath,original.ImagePath,StringComparison.OrdinalIgnoreCase))drift.Add(original.Name);
                if(protecting&&rebooted&&service!=null&&service.Running)drift.Add(original.Name);
            }
            foreach(var startup in s.Startups) {
                try {
                    var currentStartup=platform.ReadStartup(startup);
                    if(protecting?currentStartup!=null:!startup.Matches(currentStartup))drift.Add(StartupLabel(startup));
                }catch(Exception error){drift.Add(StartupLabel(startup)+"（"+error.Message+"）");}
            }
            if(protecting) {
                foreach(var startup in scan.Startups)drift.Add(StartupLabel(startup));
                if(rebooted)foreach(var app in scan.Software.Where(app=>app.ProcessRunning))
                    drift.Add(app.Name+"程序进程仍在运行");
            }
            foreach(var record in s.Devices) {
                if(!protecting&&!rebooted&&s.PendingDevices.Contains(record.InstanceId,StringComparer.OrdinalIgnoreCase))continue;
                var device=current.FirstOrDefault(d=>string.Equals(d.InstanceId,ActiveId(record),StringComparison.OrdinalIgnoreCase));
                if(device!=null&&(!Catalog.Find(record.TargetId).Matches(device)||!AliasParentMatches(record,device,current,s))){drift.Add(Catalog.Find(record.TargetId).Name);continue;}
                if(protecting&&record.DriverOnly) {
                    if(rebooted&&device!=null&&device.Started)drift.Add(Catalog.Find(record.TargetId).Name);
                    continue;
                }
                bool wanted=protecting||record.OriginallyDisabled;
                if(device==null){if(!wanted)drift.Add(Catalog.Find(record.TargetId).Name);}
                else if(device.Disabled!=wanted)drift.Add(Catalog.Find(record.TargetId).Name);
                else if(protecting&&rebooted&&device.Started)drift.Add(Catalog.Find(record.TargetId).Name);
                else if(!protecting&&rebooted&&!wanted) {
                    var target=Catalog.Find(record.TargetId);
                    bool disabledBaselineDriver=s.Drivers.Any(d=>d.Start==4&&target.DriverServices.Contains(d.Name,StringComparer.OrdinalIgnoreCase));
                    if(!disabledBaselineDriver&&(device.Problem!=0||(record.OriginallyStarted&&!device.Started)))drift.Add(target.Name);
                }
            }
            if(protecting)foreach(var device in current.Where(d=>!s.Devices.Any(r=>string.Equals(ActiveId(r),d.InstanceId,StringComparison.OrdinalIgnoreCase)))) {
                var target=Catalog.Targets.FirstOrDefault(t=>t.Matches(device));
                if(target!=null&&(!device.Disabled||(rebooted&&device.Started)))drift.Add(target.Name);
            }
            return drift.Distinct().ToList();
        }
        private static string ImageExe(string image)
        {
            if(string.IsNullOrWhiteSpace(image))return "";
            string p=image.Trim();
            if(p.StartsWith("\"")) { int end=p.IndexOf('"',1);if(end<0)return "";p=p.Substring(1,end-1); }
            else { int end=p.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);if(end>=0)p=p.Substring(0,end+4); }
            return Path.GetFileName(p);
        }
        private void CheckService(ServiceState s,bool app)
        {
            if(s==null)return;
            if(s.Start<0||s.Start>4)throw new InvalidDataException("服务启动类型无效："+s.Name);
            if(app) {
                if(!Catalog.AppExeMatches(s.Name,ImageExe(s.ImagePath)))
                    throw new InvalidDataException("远控服务身份不匹配："+s.Name);
            } else {
                if(!Catalog.DriverNames.Contains(s.Name,StringComparer.OrdinalIgnoreCase) ||
                   !string.Equals(Path.GetFileName(s.ImagePath??""),s.Name+".sys",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("驱动服务身份不匹配："+s.Name);
            }
        }
        private void Validate(Snapshot s)
        {
            if(s==null||(s.Version!=1&&s.Version!=2)||s.MachineId!=platform.MachineId||s.Devices==null||s.Drivers==null||s.Apps==null||s.Startups==null||s.PendingDevices==null||
               (s.Version==1&&s.Startups.Count>0)||
               s.Devices.Count>128||s.Drivers.Count>Catalog.DriverNames.Count()||s.Apps.Count>Catalog.AppServices.Count||s.Startups.Count>64||
               !(new[]{"Preparing","Protected","Restoring","Restored","RecoveryRequired"}).Contains(s.Phase))
                throw new InvalidDataException("备份格式或电脑身份不匹配，未执行切换。");
            if(s.PendingDevices.Count>s.Devices.Count||s.PendingDevices.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.PendingDevices.Count||s.PendingDevices.Any(id=>!s.Devices.Any(d=>string.Equals(d.InstanceId,id,StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("待恢复设备记录与原始备份不一致。");
            if(s.Devices.Any(d=>d==null||Catalog.Find(d.TargetId)==null||string.IsNullOrWhiteSpace(d.InstanceId)) ||
               s.Devices.Select(d=>d.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Devices.Count ||
               s.Devices.Select(ActiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Devices.Count ||
               s.Drivers.Select(d=>d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Drivers.Count ||
               s.Apps.Select(d=>d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Apps.Count)
                throw new InvalidDataException("备份包含未知或重复目标，未执行切换。");
            foreach(var record in s.Devices.Where(r=>!string.IsNullOrEmpty(r.CurrentInstanceId)&&!string.Equals(r.CurrentInstanceId,r.InstanceId,StringComparison.OrdinalIgnoreCase)))
                if(string.IsNullOrWhiteSpace(record.CurrentInstanceId)||record.OriginallyDisabled||s.Devices.Count(r=>r.TargetId==record.TargetId)!=1||s.Devices.Any(r=>r!=record&&string.Equals(r.InstanceId,record.CurrentInstanceId,StringComparison.OrdinalIgnoreCase))||!s.Devices.Any(r=>string.Equals(r.InstanceId,record.ParentId,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("重新生成的设备缺少唯一身份或受管父级证据。");
            foreach(var x in s.Drivers)CheckService(x,false);
            foreach(var x in s.Apps)CheckService(x,true);
            foreach(var startup in s.Startups)WindowsStartup.Validate(startup);
            if(s.Startups.Select(startup=>startup.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Startups.Count)
                throw new InvalidDataException("备份中有重复的登录启动项。");
            foreach(var record in s.Devices.Where(r=>r.DriverOnly)) {
                var names=Catalog.Find(record.TargetId).DriverServices.ToList();
                if(names.Count==0||names.Any(name=>!s.Drivers.Any(d=>string.Equals(d.Name,name,StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("重启后停用的设备缺少专用驱动备份："+record.TargetId);
            }
        }
        private Snapshot Capture(List<StartupEntry> startups)
        {
            var found=platform.Devices();
            var s=new Snapshot { Version=startups.Count>0?2:1,MachineId=platform.MachineId,CreatedUtc=DateTime.UtcNow.ToString("o"),AppliedBoot=platform.BootId,Phase="Preparing",Startups=startups };
            foreach(var t in Catalog.Targets) {
                var devices=found.Where(t.Matches).ToList();
                if(devices.Count>8)throw new InvalidDataException("同类设备过多，请核对："+t.Name);
                foreach(var d in devices)s.Devices.Add(new DeviceRecord { TargetId=t.Id,InstanceId=d.InstanceId,ParentId=ManagedParent(d,found),OriginallyDisabled=d.Disabled,OriginallyStarted=d.Started });
                if(devices.Count>0)foreach(var serviceName in t.DriverServices) {
                    var driver=platform.Service(serviceName);
                    if(driver!=null&&!s.Drivers.Any(x=>x.Name.Equals(driver.Name,StringComparison.OrdinalIgnoreCase))) { CheckService(driver,false);s.Drivers.Add(driver); }
                }
            }
            foreach(string name in Catalog.AppServices.Keys) {
                var app=platform.Service(name);if(app!=null&&Catalog.AppExeMatches(name,ImageExe(app.ImagePath))){CheckService(app,true);s.Apps.Add(app);}
            }
            foreach(var record in s.Devices) {
                var device=found.Single(d=>string.Equals(d.InstanceId,record.InstanceId,StringComparison.OrdinalIgnoreCase));
                var names=Catalog.Find(record.TargetId).DriverServices.ToList();
                record.DriverOnly=!record.OriginallyDisabled&&(device.RawStatus&8)!=0&&(device.RawStatus&0x2000)==0&&names.Count>0&&names.All(name=>s.Drivers.Any(d=>string.Equals(d.Name,name,StringComparison.OrdinalIgnoreCase)));
            }
            if(s.Devices.Count==0&&s.Apps.Count==0&&s.Startups.Count==0)throw new InvalidOperationException("扫描未发现可切换的远控设备、服务或登录启动项。");
            Validate(s);return s;
        }
        private Device Locate(DeviceRecord record,Snapshot snapshot,List<Device> knownDevices=null)
        {
            var all=knownDevices??platform.Devices();
            var device=all.FirstOrDefault(d=>string.Equals(d.InstanceId,ActiveId(record),StringComparison.OrdinalIgnoreCase));
            if(device!=null&&(!Catalog.Find(record.TargetId).Matches(device)||!AliasParentMatches(record,device,all,snapshot)))throw new InvalidOperationException("设备身份或父级已变化："+ActiveId(record));
            return device;
        }
        private static bool AliasParentMatches(DeviceRecord record,Device device,List<Device> all,Snapshot snapshot)
        {
            if(string.IsNullOrEmpty(record.CurrentInstanceId)||string.Equals(record.CurrentInstanceId,record.InstanceId,StringComparison.OrdinalIgnoreCase))return true;
            var parent=snapshot.Devices.FirstOrDefault(r=>string.Equals(r.InstanceId,record.ParentId,StringComparison.OrdinalIgnoreCase));
            if(parent==null)return false;
            if(all.Any(d=>string.Equals(d.InstanceId,record.InstanceId,StringComparison.OrdinalIgnoreCase)))return false;
            var currentParent=all.FirstOrDefault(d=>string.Equals(d.InstanceId,ActiveId(parent),StringComparison.OrdinalIgnoreCase));
            if(currentParent==null||!Catalog.Find(parent.TargetId).Matches(currentParent))return false;
            var siblings=all.Where(Catalog.Find(record.TargetId).Matches).Where(d=>string.Equals(ManagedParent(d,all),ActiveId(parent),StringComparison.OrdinalIgnoreCase)).ToList();
            return siblings.Count==1&&string.Equals(siblings[0].InstanceId,device.InstanceId,StringComparison.OrdinalIgnoreCase);
        }
        public Snapshot ReconcileRestoredDevices(Snapshot snapshot,ScanResult scan)
        {
            if(snapshot==null||snapshot.Phase!="Restored")return snapshot;
            Validate(snapshot);
            foreach(var record in snapshot.Devices)LocateForRestore(record,snapshot,scan.Devices);
            return snapshot;
        }
        private Device LocateForRestore(DeviceRecord record,Snapshot snapshot,List<Device> knownDevices=null)
        {
            var all=knownDevices??platform.Devices();
            var existing=Locate(record,snapshot,all);if(existing!=null||record.OriginallyDisabled||snapshot.Devices.Count(r=>r.TargetId==record.TargetId)!=1)return existing;
            var parent=snapshot.Devices.FirstOrDefault(r=>string.Equals(r.InstanceId,record.ParentId,StringComparison.OrdinalIgnoreCase));
            if(parent==null)return null;
            var currentParent=all.FirstOrDefault(d=>string.Equals(d.InstanceId,ActiveId(parent),StringComparison.OrdinalIgnoreCase));
            if(currentParent==null||!Catalog.Find(parent.TargetId).Matches(currentParent))return null;
            var target=Catalog.Find(record.TargetId);
            var original=all.FirstOrDefault(d=>string.Equals(d.InstanceId,record.InstanceId,StringComparison.OrdinalIgnoreCase));
            if(original!=null&&(!target.Matches(original)||!string.Equals(ManagedParent(original,all),ActiveId(parent),StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("原设备实例已出现但身份或父级不一致："+record.InstanceId);
            var candidates=all.Where(target.Matches).Where(d=>!snapshot.Devices.Any(r=>r!=record&&(string.Equals(ActiveId(r),d.InstanceId,StringComparison.OrdinalIgnoreCase)||string.Equals(r.InstanceId,d.InstanceId,StringComparison.OrdinalIgnoreCase)))).Where(d=>string.Equals(ManagedParent(d,all),ActiveId(parent),StringComparison.OrdinalIgnoreCase)).ToList();
            if(candidates.Count>1)throw new InvalidOperationException("同一父设备下存在多个匹配项，无法确认要恢复的设备："+target.Name);
            if(candidates.Count==0)return null;
            string previous=ActiveId(record);record.CurrentInstanceId=candidates[0].InstanceId;
            Validate(snapshot);store.Save(snapshot);Note("已核验重新生成的设备实例："+target.Name+"；"+previous+" → "+record.CurrentInstanceId);
            return candidates[0];
        }
        private static string ManagedParent(Device device,List<Device> all)
        {
            string parent=device.ParentId;var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(!string.IsNullOrEmpty(parent)&&visited.Add(parent)) {
                var candidate=all.FirstOrDefault(d=>string.Equals(d.InstanceId,parent,StringComparison.OrdinalIgnoreCase));
                if(candidate==null)break;
                if(Catalog.Targets.Any(t=>t.Matches(candidate)))return candidate.InstanceId;
                parent=candidate.ParentId;
            }
            return device.ParentId;
        }
        private static int Depth(DeviceRecord record,List<DeviceRecord> all)
        {
            int depth=0;var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var current=record;
            while(current!=null&&visited.Add(current.InstanceId)&&depth<128){current=all.FirstOrDefault(d=>string.Equals(d.InstanceId,current.ParentId,StringComparison.OrdinalIgnoreCase));if(current!=null)depth++;}
            return depth;
        }
        private void StartValue(ServiceState original,int start,bool app)
        {
            var current=platform.Service(original.Name);
            if(current==null)throw new InvalidOperationException("服务已不存在："+original.Name);
            CheckService(current,app);
            if(!string.Equals(current.ImagePath,original.ImagePath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务路径已变化，需要核对："+original.Name);
            if(current.Start==start)return;
            Note("设置 "+original.Name+" 启动值 "+current.Start+" → "+start);
            platform.SetStart(original.Name,start);
            var result=platform.Service(original.Name);
            if(result==null||result.Start!=start)throw new IOException("启动配置写入后验证失败："+original.Name);
        }
        private static string StartupLabel(StartupEntry entry)
        {return "登录启动项 "+entry.Name+"（"+(entry.Scope=="User"?"当前账户":"电脑 "+entry.View+" 位")+"）";}
        private StartupEntry VerifyStartupSlot(StartupEntry startup)
        {
            var current=platform.ReadStartup(startup);
            if(current!=null&&!startup.Matches(current))throw new IOException(StartupLabel(startup)+"与原备份不同，请先核对。");
            return current;
        }
        private void SuppressStartup(StartupEntry startup)
        {
            if(VerifyStartupSlot(startup)==null)return;
            platform.SetStartup(startup,false);
            Note("已停用"+StartupLabel(startup));
        }
        private void RestoreStartups(List<StartupEntry> startups,List<string> failures)
        {
            foreach(var startup in startups) {
                try {
                    platform.SetStartup(startup,true);
                    Note("已恢复"+StartupLabel(startup));
                }catch(Exception error){failures.Add(StartupLabel(startup)+"恢复未完成："+error.Message);}
            }
        }
        public Snapshot Protect()
        {
            platform.EnsureReady();
            var previous=Current();
            if(previous!=null&&(previous.Phase=="RecoveryRequired"||previous.Phase=="Restoring"||previous.Phase=="Preparing"))
                throw new InvalidOperationException("上次切换尚未完成，请先使用“恢复远控”。");
            if(previous!=null&&previous.PendingDevices.Count>0)throw new InvalidOperationException("上次恢复还有待完成的设备，请先完成恢复和重启核验。");
            if(previous!=null&&previous.Phase=="Restored"&&previous.AppliedBoot==platform.BootId&&!CanProtect(previous,ScanResult.Capture(platform)))
                throw new InvalidOperationException("恢复后的运行状态尚未回到原始配置，请先完成重启，再进入下一次游戏模式。");
            var startups=platform.StartupEntries();
            var s=previous!=null&&previous.Phase=="Protected"?previous:Capture(startups);
            if(s==previous)foreach(var startup in startups) {
                var original=s.Startups.FirstOrDefault(entry=>StringComparer.OrdinalIgnoreCase.Equals(entry.Identity,startup.Identity));
                if(original==null)s.Startups.Add(startup);
                else if(!original.Matches(startup))throw new IOException(StartupLabel(startup)+"与原备份不同，请先核对。");
            }
            if(s.Startups.Count>0)s.Version=2;
            Validate(s);
            var presentStartups=s.Startups.Where(startup=>VerifyStartupSlot(startup)!=null).ToList();
            foreach(var record in s.Devices)Locate(record,s);
            var appsBeforeAttempt=s.Apps.Select(app=>platform.Service(app.Name)).ToList();
            foreach(var app in appsBeforeAttempt){if(app==null)throw new InvalidOperationException("远控服务在切换前已变化，请重新扫描。");CheckService(app,true);}
            string phaseBeforeAttempt=s.Phase;bool driverStage=false;
            // The original snapshot must be durable before the first system change.
            store.Save(s);
            try {
                foreach(var startup in s.Startups)SuppressStartup(startup);
                foreach(var app in s.Apps)StartValue(app,4,true);
                foreach(var app in s.Apps) {
                    if(platform.Service(app.Name).Running){Note("停止远控服务："+app.Name);platform.StopApp(app.Name);}
                }
                Note("关闭 UU、ToDesk 和向日葵的后台进程，并确认退出。");
                platform.CloseRemoteProcesses();
                foreach(var startup in s.Startups)SuppressStartup(startup);
                var outstanding=platform.StartupEntries();
                if(outstanding.Count>0)throw new IOException("退出后仍发现远控登录启动项，请重新扫描："+string.Join("、",outstanding.Select(StartupLabel)));
                Note("远控后台进程检查完成，开始处理驱动。");
                driverStage=true;
                foreach(var d in s.Drivers)StartValue(d,4,false);
                foreach(var record in s.Devices.OrderByDescending(r=>Depth(r,s.Devices)).ThenBy(r=>Catalog.Find(r.TargetId).Order)) {
                    if(record.OriginallyDisabled)continue;
                    if(record.DriverOnly) {
                        if(Catalog.Find(record.TargetId).DriverServices.Any(name=>platform.Service(name)==null||platform.Service(name).Start!=4))throw new IOException("专用驱动停用配置未通过验证："+record.TargetId);
                        Note(Catalog.Find(record.TargetId).Name+"不支持在线禁用；专用驱动已设置禁用，重启后核验停止加载。");continue;
                    }
                    var device=Locate(record,s);
                    if(device==null){Note("父级停用后设备已退出枚举："+record.InstanceId);continue;}
                    if(!device.Disabled){Note("禁用设备："+Catalog.Find(record.TargetId).Name);platform.SetDevice(device,false);}
                }
                s.Phase="Protected";s.AppliedBoot=platform.BootId;store.Save(s);
                Note("游戏配置已保存，等待重启生效。");return s;
            } catch(PendingOperationException error) {
                s.Phase="RecoveryRequired";store.Save(s);Note(error.Message);throw;
            } catch(Exception error) {
                Note("切换失败："+error.Message+"；开始按原状态回退。");
                try {
                    if(driverStage)RestoreInternal(s,true);
                    else {
                        var failures=new List<string>();
                        try {
                            foreach(var app in appsBeforeAttempt)StartValue(app,app.Start,true);
                            foreach(var app in appsBeforeAttempt)if(!app.Running&&platform.Service(app.Name).Running)platform.StopApp(app.Name);
                            foreach(var app in appsBeforeAttempt)if(app.Running&&app.Start!=4&&!platform.Service(app.Name).Running)platform.StartApp(app.Name);
                        }catch(Exception rollback){failures.Add(rollback.Message);}
                        RestoreStartups(presentStartups,failures);
                        if(failures.Count>0)throw new IOException(string.Join("；",failures));
                        s.Phase=phaseBeforeAttempt=="Protected"?"Protected":"Restored";store.Save(s);
                        Note("驱动阶段尚未开始；已恢复本次操作前的登录启动项和远控服务配置。");
                    }
                }
                catch(Exception rollback) {s.Phase="RecoveryRequired";store.Save(s);Note("回退未完成："+rollback.Message);}
                throw new InvalidOperationException("切换失败，未安排重启。"+error.Message);
            }
        }
        public Snapshot Restore()
        {
            platform.EnsureReady();
            var s=Current();if(s==null)throw new InvalidOperationException("尚无切换前的备份，请先使用游戏模式。");
            foreach(var record in s.Devices)Locate(record,s);
            try {RestoreInternal(s,false);return s;}
            catch(Exception e){s.Phase="RecoveryRequired";store.Save(s);Note("恢复未完成："+e.Message);throw;}
        }
        public Snapshot ContinueRestoreAfterReboot()
        {
            var original=Current();
            if(original==null||original.Phase!="Restored"||original.PendingDevices.Count==0||original.AppliedBoot==platform.BootId)throw new InvalidOperationException("当前没有重启后可继续的恢复任务。");
            string priorBoot=original.AppliedBoot;
            var restored=Restore();
            if(restored.PendingDevices.Count==0){restored.AppliedBoot=priorBoot;store.Save(restored);Note("重启后待恢复设备已处理，开始核验实际运行状态。");}
            return restored;
        }
        private void Pending(Snapshot s,DeviceRecord record)
        {
            if(!s.PendingDevices.Contains(record.InstanceId,StringComparer.OrdinalIgnoreCase))s.PendingDevices.Add(record.InstanceId);
            store.Save(s);
            Note("已安排重启后继续恢复："+Catalog.Find(record.TargetId).Name);
        }
        private void RestoreInternal(Snapshot s,bool rollback)
        {
            Validate(s);s.Phase="Restoring";s.PendingDevices.Clear();store.Save(s);
            // Restore driver start values before enabling the virtual HID parent.
            foreach(var d in s.Drivers)StartValue(d,d.Start,false);
            var failures=new List<string>();
            var ordered=s.Devices.OrderBy(r=>Depth(r,s.Devices)).ThenByDescending(r=>Catalog.Find(r.TargetId).Id=="uu-hid"?100:Catalog.Find(r.TargetId).Order).ToList();
            var deferred=new List<DeviceRecord>();
            foreach(var record in ordered) {
                try {
                    var device=LocateForRestore(record,s);
                    if(device==null){
                        if(record.OriginallyDisabled)continue;
                        if(Depth(record,s.Devices)>0||record.TargetId.EndsWith("-filter"))deferred.Add(record);
                        else failures.Add("设备未重新出现："+Catalog.Find(record.TargetId).Name);
                        continue;
                    }
                    bool enabled=!record.OriginallyDisabled;
                    bool reactivate=enabled&&record.OriginallyStarted&&!device.Started&&device.Problem==32;
                    if(device.Disabled!=record.OriginallyDisabled||reactivate){Note((enabled?"启用设备：":"恢复原禁用状态：")+Catalog.Find(record.TargetId).Name);platform.SetDevice(device,enabled);if(device.RebootRequired)Pending(s,record);}
                }catch(Exception e){failures.Add(e.Message);}
            }
            // Keep app services paused while restoring a failed device transition.
            if(failures.Count>0)throw new InvalidOperationException(string.Join("；",failures));
            foreach(var app in s.Apps)StartValue(app,app.Start,true);
            // Some virtual children are created only after the product service opens the HID parent.
            foreach(var app in s.Apps)if(app.Running&&app.Start!=4){
                try{platform.StartApp(app.Name);}
                catch(Exception error){if(s.PendingDevices.Count==0)throw;Note("服务启动等待设备重启恢复："+app.Name+"；"+error.Message);}
            }
            foreach(var record in deferred) {
                bool waitingForParent=s.PendingDevices.Contains(record.ParentId,StringComparer.OrdinalIgnoreCase);
                if(waitingForParent){Pending(s,record);continue;}
                Device device=null;
                for(int attempt=0;attempt<25&&device==null;attempt++) {
                    device=LocateForRestore(record,s);
                    if(device==null)System.Threading.Thread.Sleep(200);
                }
                if(device==null)throw new InvalidOperationException(Catalog.Find(record.TargetId).Name+"尚未重新枚举，请重启后再次恢复。原始备份仍保留。");
                if(device.Disabled!=record.OriginallyDisabled){Note((record.OriginallyDisabled?"恢复原禁用状态：":"启用设备：")+Catalog.Find(record.TargetId).Name);platform.SetDevice(device,!record.OriginallyDisabled);if(device.RebootRequired)Pending(s,record);}
            }
            RestoreStartups(s.Startups,failures);
            if(failures.Count>0)throw new IOException(string.Join("；",failures));
            s.Phase="Restored";s.AppliedBoot=platform.BootId;store.Save(s);
            Note(s.PendingDevices.Count==0?"原始配置已恢复，重启后按原启动方式加载。":"恢复配置已保存，重启后继续处理 "+s.PendingDevices.Count+" 个设备并核验。");
        }
        public void Restart() {Note("用户确认重启，调用 Windows 正常重启。");platform.Reboot();}
    }
}
