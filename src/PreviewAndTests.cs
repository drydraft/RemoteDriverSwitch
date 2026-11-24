using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace RemoteDriverSwitch
{
    public sealed class MemoryStore : IStore
    {
        public Snapshot State;public List<string> Logs=new List<string>();public bool FailSave;
        public Snapshot Load(){return Clone(State);}
        public void Save(Snapshot s){if(FailSave)throw new IOException("模拟备份失败");State=Clone(s);}
        public void Log(string text){Logs.Add(text);}
        private static Snapshot Clone(Snapshot s){return s==null?null:new JavaScriptSerializer().Deserialize<Snapshot>(new JavaScriptSerializer().Serialize(s));}
    }
    public sealed class FakePlatform : IPlatform
    {
        public string MachineKey="SIMULATION-ONLY";
        public string MachineId {get{return MachineKey;}}
        public string BootId {get;set;}
        public List<Device> Items=new List<Device>();
        public Dictionary<string,ServiceState> Services=new Dictionary<string,ServiceState>(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls=new List<string>();
        public readonly List<StartupEntry> StartupValues=new List<StartupEntry>();
        public Action<StartupEntry,bool> BeforeStartupChange;
        public Action ClosingRemoteProcesses;
        public static readonly string[] DefaultTargetIds={"uu-hid","uu-filter","uu-display","oray-usb","oray-gamepad","oray-display","td-audio"};
        public string FailOnDevice,ProcessCloseHideTarget,FailOnAppStart;public bool FailOnce=true,Pending,Busy,RebootFail,HideFilterUntilServiceRuns,HideChildrenWithDisabledParent,ProcessCloseFailure,HideFilterUntilDriverRuns,FailAppStartUntilDriverLoaded;
        public int Reboots,DeviceScans,ServiceReads,SoftwareScans;
        public Dictionary<string,string> DeferredUntilAppRuns=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeferredEnableTargets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public FakePlatform()
        {
            BootId="boot-1";
            foreach(string id in DefaultTargetIds)AddProfile(id);
            Items.Single(d=>d.InstanceId=="SIM\\uu-filter").ParentId="SIM\\uu-hid";
            foreach(var pair in Catalog.AppServices)AddApp(pair.Key,"D:\\RemoteApps\\"+pair.Value,pair.Key=="GameViewerService"?2:3,pair.Key=="GameViewerService");
        }
        public Device AddProfile(string id,string instanceId=null)
        {
            var t=Catalog.Find(id);if(t==null)throw new ArgumentException("Unknown simulation profile: "+id);
            var d=new Device {InstanceId=instanceId??"SIM\\"+t.Id,Name=t.Name,Service=t.DeviceService,Provider=t.Provider,HardwareIds=new[]{t.HardwareId},Inf="oem-example.inf",DriverVersion="1.0",Started=true,Problem=0,RawStatus=0x0180200B};Items.Add(d);
            foreach(string service in t.DriverServices)if(!Services.ContainsKey(service))Services.Add(service,new ServiceState {Name=service,Start=service=="OrayVGC"?1:3,ImagePath="C:\\Windows\\System32\\drivers\\"+service+".sys",Running=true});
            return d;
        }
        public void AddApp(string name,string executable,int start=3,bool running=false)
        {Services[name]=new ServiceState {Name=name,Start=start,ImagePath="\""+executable+"\" --service",Running=running};}
        public void EnsureReady(){if(Busy)throw new PendingOperationException("模拟设备操作仍在进行");}
        public List<Device> Devices()
        {
            DeviceScans++;
            return Items.Where(d=>{
                string appName;ServiceState appService;
                if(DeferredUntilAppRuns.TryGetValue(d.InstanceId,out appName)&&(!Services.TryGetValue(appName,out appService)||!appService.Running))return false;
                if(HideChildrenWithDisabledParent) {
                    var parent=Items.FirstOrDefault(x=>string.Equals(x.InstanceId,d.ParentId,StringComparison.OrdinalIgnoreCase));
                    var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while(parent!=null&&visited.Add(parent.InstanceId)) {
                        if(parent.Disabled)return false;
                        parent=Items.FirstOrDefault(x=>string.Equals(x.InstanceId,parent.ParentId,StringComparison.OrdinalIgnoreCase));
                    }
                }
                var t=Catalog.Targets.FirstOrDefault(x=>x.Matches(d));
                if(HideFilterUntilDriverRuns&&t!=null&&t.Id=="uu-filter"&&!Services["uuycinput"].Running)return false;
                if(!HideFilterUntilServiceRuns||t==null||!t.Id.EndsWith("-filter"))return true;
                string app=t.ProductId=="uu"?"GameViewerService":t.ProductId=="td"?"ToDesk_Service":"AweSunService";ServiceState s;
                return Services.TryGetValue(app,out s)&&s.Running;
            }).ToList();
        }
        public List<SoftwareInfo> Software(List<Device> devices)
        {
            SoftwareScans++;
            var result=new List<SoftwareInfo>();
            foreach(string id in new[]{"uu","oray","td"}) {
                var apps=Catalog.AppServices.Keys.Where(k=>Catalog.AppProduct(k)==id&&Services.ContainsKey(k)).Select(k=>Services[k]).ToList();
                bool installed=apps.Count>0||devices.Any(d=>Catalog.DeviceProduct(d)==id);string image=apps.Count>0?apps[0].ImagePath:"";string exe=image.StartsWith("\"")?image.Split('"')[1]:image;
                result.Add(new SoftwareInfo {Id=id,Name=id=="uu"?"UU远程":id=="oray"?"向日葵":"ToDesk",Version=installed?"simulation-1.0":"",InstallPath=string.IsNullOrEmpty(exe)?"":Path.GetDirectoryName(exe),Installed=installed,ServiceDetected=apps.Count>0,ServiceRunning=apps.Any(a=>a.Running),Evidence=new List<string>{installed?"模拟服务或已识别设备":"未发现模拟安装"}});
            }
            return result;
        }
        public ServiceState Service(string name){ServiceReads++;ServiceState s;if(!Services.TryGetValue(name,out s))return null;return new ServiceState {Name=s.Name,ImagePath=s.ImagePath,Start=s.Start,Running=s.Running};}
        public void SetStart(string name,int start){Calls.Add("start:"+name+":"+start);Services[name].Start=start;}
        public void StopApp(string name){Calls.Add("stop:"+name);Services[name].Running=false;}
        public void StartApp(string name){Calls.Add("run:"+name);if(name==FailOnAppStart)throw new IOException("模拟远控服务启动失败");if(FailAppStartUntilDriverLoaded&&name=="GameViewerService"&&!Services["uuycinput"].Running)throw new IOException("模拟远控服务依赖尚未重新加载的驱动");Services[name].Running=true;}
        private static bool SameStartupSlot(StartupEntry first,StartupEntry second)
        {
            return first.Scope==second.Scope&&first.View==second.View&&
                string.Equals(first.UserSid,second.UserSid,StringComparison.OrdinalIgnoreCase)&&
                string.Equals(first.Name,second.Name,StringComparison.OrdinalIgnoreCase);
        }
        private static StartupEntry CopyStartup(StartupEntry entry)
        {
            return new StartupEntry {
                Scope=entry.Scope,View=entry.View,UserSid=entry.UserSid,Name=entry.Name,
                Command=entry.Command,ProductId=entry.ProductId,Kind=entry.Kind
            };
        }
        public List<StartupEntry> StartupEntries()
        {
            return StartupValues.Where(entry=>entry.ProductId=="uu"||entry.ProductId=="td"||entry.ProductId=="oray").Select(CopyStartup).ToList();
        }
        public StartupEntry ReadStartup(StartupEntry entry)
        {
            var current=StartupValues.SingleOrDefault(value=>SameStartupSlot(value,entry));
            if(current==null)return null;
            var raw=CopyStartup(current);raw.ProductId=null;return raw;
        }
        public void SetStartup(StartupEntry entry,bool enabled)
        {
            if(BeforeStartupChange!=null)BeforeStartupChange(entry,enabled);
            var current=ReadStartup(entry);
            if(current!=null&&(current.Kind!=entry.Kind||current.Command!=entry.Command))
                throw new IOException("模拟启动项内容已变化");
            Calls.Add("startup:"+entry.Name+":"+enabled);
            if(enabled&&current==null)StartupValues.Add(CopyStartup(entry));
            if(!enabled&&current!=null)StartupValues.RemoveAll(value=>SameStartupSlot(value,entry));
        }
        public void CloseRemoteProcesses(){Calls.Add("close-processes");if(ClosingRemoteProcesses!=null)ClosingRemoteProcesses();if(ProcessCloseHideTarget!=null)Items.RemoveAll(d=>d.InstanceId==ProcessCloseHideTarget);if(ProcessCloseFailure)throw new IOException("模拟远控进程仍被占用或反复重启");}
        public void SetDevice(Device d,bool enabled)
        {
            Calls.Add((enabled?"enable:":"disable:")+d.InstanceId);
            if(d.InstanceId==FailOnDevice&&FailOnce){FailOnce=false;if(Pending)throw new PendingOperationException("模拟超时");throw new IOException("模拟设备切换失败");}
            d.Disabled=!enabled;d.ConfigFlags=enabled?0:1;
            if(enabled&&DeferredEnableTargets.Contains(d.InstanceId)){d.Started=false;d.Problem=14;d.RawStatus&=~8U;d.RebootRequired=true;return;}
            d.Started=enabled;d.Problem=enabled?0U:22U;d.RawStatus=enabled?(d.RawStatus|8U):(d.RawStatus&~8U);d.RebootRequired=false;
            if(enabled)foreach(var target in Catalog.Targets.Where(t=>t.Matches(d)))foreach(string driver in target.DriverServices)if(Services.ContainsKey(driver)&&Services[driver].Start!=4)Services[driver].Running=true;
        }
        public void Reboot(){if(RebootFail)throw new IOException("模拟重启失败");Reboots++;}
    }
    public static class Tests
    {
        private static void Check(bool condition,string text){if(!condition)throw new Exception(text);}
        private static void Throws(Action action,string text){bool thrown=false;try{action();}catch{thrown=true;}Check(thrown,text);}
        private static StartupEntry UuStartup(string name="GameViewer")
        {
            return new StartupEntry {
                Scope="User",View=64,UserSid="S-1-5-21-111-222-333-1001",Name=name,ProductId="uu",
                Command="\"%ProgramFiles%\\Sample Apps\\UU\\GameViewer.exe\" --autostart",Kind=(int)Microsoft.Win32.RegistryValueKind.ExpandString
            };
        }
        private static FakePlatform OfflineProtectedUu(out MemoryStore store,out SwitchEngine engine)
        {
            var platform=new FakePlatform {HideFilterUntilDriverRuns=true};
            var root=platform.Items.Single(d=>d.InstanceId=="SIM\\uu-hid");root.RawStatus=0x0180000B;
            store=new MemoryStore();engine=new SwitchEngine(platform,store);engine.Protect();
            platform.BootId="boot-2";foreach(var service in platform.Services.Values)service.Running=false;
            root.Started=false;root.Problem=32;root.RawStatus&=~8U;
            platform.Calls.Clear();return platform;
        }
        private static Device RecreatedUuFilter(FakePlatform platform,string instanceId,bool disabled=true)
        {
            platform.Items.RemoveAll(d=>Catalog.Find("uu-filter").Matches(d));
            var child=platform.AddProfile("uu-filter",instanceId);child.ParentId="SIM\\uu-hid";
            child.Disabled=disabled;child.Started=!disabled;child.Problem=disabled?22U:0U;
            child.ConfigFlags=disabled?1:0;child.RawStatus=disabled?(child.RawStatus&~8U):(child.RawStatus|8U);
            return child;
        }
        public static int Run(string reportPath)
        {
            var results=new List<string>();
            Action<string,Action> test=(name,action)=>{try{action();results.Add("PASS "+name);}catch(Exception e){results.Add("FAIL "+name+": "+e);}};
            test("release update compares numeric versions and skips older or current releases",()=>{
                const string response="{\"tag_name\":\"v1.1.10\",\"draft\":false,\"prerelease\":false}";
                Check(ReleaseUpdates.Parse(response,new Version(1,1,9,0))==new Version(1,1,10),"newer release was missed");
                Check(ReleaseUpdates.Parse(response,new Version(1,1,10,0))==null,"current release was offered as an update");
                Check(ReleaseUpdates.Parse(response,new Version(1,2,0,0))==null,"older release was offered as an update");
            });
            test("release update ignores drafts and prereleases",()=>{
                var installed=new Version(1,1,6,0);
                Check(ReleaseUpdates.Parse("{\"tag_name\":\"v1.2.0\",\"draft\":true,\"prerelease\":false}",installed)==null,"draft was offered as an update");
                Check(ReleaseUpdates.Parse("{\"tag_name\":\"v1.2.0-rc.1\",\"draft\":false,\"prerelease\":true}",installed)==null,"prerelease was offered as an update");
            });
            test("release update rejects invalid metadata and uses the official release page",()=>{
                var installed=new Version(1,1,6,0);
                Throws(()=>ReleaseUpdates.Parse("{\"tag_name\":\"v01.2.0\",\"draft\":false,\"prerelease\":false}",installed),"noncanonical tag was accepted");
                Throws(()=>ReleaseUpdates.Parse("{\"tag_name\":\"v1.2.0\",\"draft\":\"false\",\"prerelease\":false}",installed),"untyped release flag was accepted");
                var release=ReleaseUpdates.Parse("{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://example.invalid\"}",installed);
                Check(ReleaseUpdates.Page(release)=="https://github.com/drydraft/RemoteDriverSwitch/releases/tag/v1.2.0","release page did not use the official repository and verified tag");
            });
            test("seven-target exact identity",()=>{var p=new FakePlatform();Check(p.Items.Count==7&&Catalog.Targets.Where(t=>FakePlatform.DefaultTargetIds.Contains(t.Id)).All(t=>p.Items.Count(t.Matches)==1),"default target identity mismatch");Check(Catalog.Targets.Where(t=>!FakePlatform.DefaultTargetIds.Contains(t.Id)).All(t=>!p.Items.Any(t.Matches)),"uninstalled profiles appeared in simulation");Check(p.Services.Keys.Count(k=>Catalog.DriverNames.Contains(k,StringComparer.OrdinalIgnoreCase))==5,"shared system services must not be modified");});
            test("protect and restore preserve baseline",()=>{var p=new FakePlatform();var s=new MemoryStore();var e=new SwitchEngine(p,s);p.Items.Single(d=>d.InstanceId=="SIM\\oray-gamepad").Disabled=true;e.Protect();Check(p.Items.All(d=>d.Disabled),"not all disabled");Check(p.Services.Values.All(v=>v.Start==4),"not all starts disabled");e.Restore();Check(p.Items.Where(d=>d.InstanceId!="SIM\\oray-gamepad").All(d=>!d.Disabled),"enabled state not restored");Check(p.Items.Single(d=>d.InstanceId=="SIM\\oray-gamepad").Disabled,"previously disabled device was enabled");Check(p.Services["OrayVGC"].Start==1&&p.Services["uuycinput"].Start==3&&p.Services["GameViewerService"].Start==2,"start values not restored");});
            test("remote services and processes close before any driver changes",()=>{var p=new FakePlatform();foreach(string name in Catalog.AppServices.Keys)p.Services[name].Running=true;var store=new MemoryStore();new SwitchEngine(p,store).Protect();int close=p.Calls.IndexOf("close-processes");Check(close>=0,"remote process close phase missing");foreach(string name in Catalog.AppServices.Keys){Check(p.Calls.IndexOf("start:"+name+":4")>=0&&p.Calls.IndexOf("start:"+name+":4")<close,"remote service can relaunch processes during close: "+name);Check(p.Calls.IndexOf("stop:"+name)>=0&&p.Calls.IndexOf("stop:"+name)<close,"running service not stopped before process close: "+name);}Check(p.Calls.Where(c=>Catalog.DriverNames.Any(n=>c=="start:"+n+":4")||c.StartsWith("disable:")).All(c=>p.Calls.IndexOf(c)>close),"driver or device modified before process close completed");Check(store.State.Apps.All(a=>a.Running),"original running service baseline was captured after service stop");});
            test("protect saves remote startup values before suppressing them and restores the original data",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);
                var uu=UuStartup();
                var other=new StartupEntry {Scope=uu.Scope,View=uu.View,UserSid=uu.UserSid,Name="UnrelatedApp",Command="C:\\Sample\\Unrelated.exe",Kind=(int)Microsoft.Win32.RegistryValueKind.String};
                p.StartupValues.AddRange(new[]{uu,other});
                p.BeforeStartupChange=(entry,enabled)=>{
                    if(!enabled)Check(store.Load().Startups.Any(value=>value.Name==entry.Name&&value.Command==entry.Command&&value.Kind==entry.Kind),"startup data was not durable before removing its Run value");
                };
                engine.Protect();
                int removed=p.Calls.IndexOf("startup:"+uu.Name+":False");
                Check(removed>=0&&removed<p.Calls.IndexOf("start:uuycinput:4"),"remote startup value was not removed before the driver phase");
                Check(p.ReadStartup(uu)==null&&p.ReadStartup(other).Command==other.Command,"game mode retained a remote startup entry or removed an unrelated entry");
                engine.Restore();
                var restored=p.ReadStartup(uu);
                Check(restored!=null&&restored.Scope==uu.Scope&&restored.View==uu.View&&restored.UserSid==uu.UserSid&&restored.Command==uu.Command&&restored.Kind==uu.Kind,"restore lost original startup scope, raw command, or registry type");
                Check(p.ReadStartup(other).Command==other.Command,"restore replaced an unrelated entry");
            });
            test("protected snapshot can add remote startup values on reapply without replacing the original baseline",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);
                var original=engine.Protect();
                store.State.Version=1;
                var first=UuStartup();p.StartupValues.Add(first);
                var firstReapply=engine.Protect();
                Check(firstReapply.Startups.Count==1&&p.ReadStartup(first)==null,"older protected snapshot missed a new startup value");
                Check(firstReapply.Version==2&&store.State.Version==2,"version 1 backup was not migrated when startup data was added");
                var second=UuStartup("GameViewerTray");p.StartupValues.Add(second);
                var secondReapply=engine.Protect();
                Check(secondReapply.CreatedUtc==original.CreatedUtc&&secondReapply.Drivers.Single(d=>d.Name=="uuycinput").Start==3,"reapply replaced the original driver baseline");
                Check(secondReapply.Startups.Count==2&&p.ReadStartup(second)==null,"reapply lost an earlier startup backup or left a newly found value enabled");
                engine.Restore();
                Check(p.ReadStartup(first).Command==first.Command&&p.ReadStartup(second).Command==second.Command,"reapplied game mode could not restore both startup values");
            });
            test("startup value recreated during client exit is suppressed again before driver changes",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);var uu=UuStartup();p.StartupValues.Add(uu);
                p.ClosingRemoteProcesses=()=>p.StartupValues.Add(uu);
                engine.Protect();
                string removal="startup:"+uu.Name+":False";
                Check(p.Calls.Count(c=>c==removal)==2&&p.Calls.IndexOf("close-processes")<p.Calls.LastIndexOf(removal),"shutdown recreated a login value that was never rechecked");
                Check(p.Calls.LastIndexOf(removal)<p.Calls.IndexOf("start:uuycinput:4")&&p.ReadStartup(uu)==null,"driver stage started while the recreated login value was present");
                Check(store.State.Startups.Count==1,"recreated slot duplicated the recovery record");
            });
            test("failed first protect restores startup run values before leaving the driver state untouched",()=>{
                var p=new FakePlatform {ProcessCloseFailure=true};var store=new MemoryStore();var engine=new SwitchEngine(p,store);var uu=UuStartup();p.StartupValues.Add(uu);
                Throws(()=>engine.Protect(),"failed process close was ignored");
                Check(p.Calls.Contains("startup:"+uu.Name+":False")&&p.Calls.Contains("startup:"+uu.Name+":True"),"startup value was not rolled back after the pre-driver failure");
                Check(p.ReadStartup(uu).Command==uu.Command&&store.State.Phase=="Restored","pre-driver failure did not restore the original startup value");
                Check(!p.Calls.Contains("start:uuycinput:4")&&p.Reboots==0,"pre-driver failure touched a driver or rebooted");
            });
            test("failed protected reapply preserves suppressed startup values and returns newly active ones",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);
                var existing=UuStartup();p.StartupValues.Add(existing);engine.Protect();
                var incoming=UuStartup("GameViewerTray");p.StartupValues.Add(incoming);p.ProcessCloseFailure=true;p.Calls.Clear();
                Throws(()=>engine.Protect(),"failed process close during reapply was ignored");
                Check(p.ReadStartup(existing)==null,"failed reapply changed a previously suppressed startup value");
                Check(p.ReadStartup(incoming).Command==incoming.Command&&store.State.Phase=="Protected","failed reapply altered the previous startup state or game mode");
                Check(p.Calls.Contains("startup:"+incoming.Name+":False")&&!p.Calls.Any(c=>c.StartsWith("disable:")||c.StartsWith("enable:")),"failed reapply skipped the startup preflight or changed device state");
            });
            test("startup rollback conflict does not prevent previously running remote services from recovering",()=>{
                var p=new FakePlatform {ProcessCloseFailure=true};var store=new MemoryStore();var engine=new SwitchEngine(p,store);
                var uu=UuStartup();p.StartupValues.Add(uu);
                var other=new StartupEntry {
                    Scope=uu.Scope,View=uu.View,UserSid=uu.UserSid,Name=uu.Name,
                    Command="C:\\Sample\\Unrelated.exe",Kind=(int)Microsoft.Win32.RegistryValueKind.String
                };
                p.ClosingRemoteProcesses=()=>p.StartupValues.Add(other);
                Throws(()=>engine.Protect(),"pre-driver failure was ignored");
                Check(p.ReadStartup(uu).Command==other.Command,"rollback overwrote the replacement startup value");
                Check(p.Services["GameViewerService"].Start==2&&p.Services["GameViewerService"].Running,"startup slot conflict prevented service rollback");
                Check(store.State.Phase=="RecoveryRequired"&&store.State.Startups.Single().Command==uu.Command&&p.Reboots==0,"rollback conflict lost its recovery data or restarted");
            });
            test("reboot verification reports reenabled or missing managed startup values",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);var uu=UuStartup();p.StartupValues.Add(uu);
                var protectedState=engine.Protect();p.BootId="boot-2";foreach(var service in p.Services.Values)service.Running=false;
                Check(engine.ConfigurationDrift(protectedState).Count==0,"suppressed startup value caused a false game mode verification failure");
                p.StartupValues.Add(uu);
                Check(engine.ConfigurationDrift(protectedState).Count>0,"reenabled startup value was hidden from game mode verification");
                var restored=engine.Restore();p.BootId="boot-3";
                Check(engine.ConfigurationDrift(restored).Count==0,"restored startup value caused a false recovery verification failure");
                p.StartupValues.Clear();
                Check(engine.ConfigurationDrift(restored).Count>0,"missing startup value was hidden from recovery verification");
            });
            test("a remote client running after reboot prevents a clean game mode result",()=>{
                var platform=new FakePlatform();var engine=new SwitchEngine(platform,new MemoryStore());var snapshot=engine.Protect();
                platform.BootId="boot-2";
                foreach(var service in platform.Services.Values)service.Running=false;
                var scan=ScanResult.Capture(platform);
                Check(engine.ConfigurationDrift(snapshot,scan).Count==0,"closed clients should allow verification");
                scan.Software.Single(app=>app.Id=="uu").ProcessRunning=true;
                Check(engine.ConfigurationDrift(snapshot,scan).Any(issue=>issue.Contains("UU远程")),"restarted client was missed");
            });
            test("startup restore preserves a changed value that reused its backed-up slot",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);
                var uu=UuStartup();p.StartupValues.Add(uu);engine.Protect();
                var other=new StartupEntry {
                    Scope=uu.Scope,View=uu.View,UserSid=uu.UserSid,Name=uu.Name,
                    Command="C:\\Sample\\Unrelated.exe",Kind=(int)Microsoft.Win32.RegistryValueKind.String
                };
                p.StartupValues.Add(other);
                Throws(()=>engine.Restore(),"conflicting startup slot was silently treated as restored");
                Check(p.ReadStartup(uu).Command==other.Command&&p.ReadStartup(uu).Kind==other.Kind,"restore overwrote the replacement startup value");
                Check(store.State.Phase=="RecoveryRequired"&&store.State.Startups.Single().Command==uu.Command&&p.Reboots==0,"startup slot conflict discarded the original backup or restarted");
                p.StartupValues.Remove(other);
                engine.Restore();
                Check(p.ReadStartup(uu).Command==uu.Command&&store.State.Phase=="Restored","removing the conflicting startup value did not allow recovery");
            });
            test("failed process close restores services without touching drivers",()=>{var p=new FakePlatform {ProcessCloseFailure=true};var store=new MemoryStore();var e=new SwitchEngine(p,store);Throws(()=>e.Protect(),"process close failure ignored");Check(p.Calls.Contains("close-processes"),"process closing was skipped");Check(!p.Calls.Any(c=>c.StartsWith("disable:")||c.StartsWith("enable:")||Catalog.DriverNames.Any(n=>c.StartsWith("start:"+n+":"))),"driver or device changed despite process close failure");Check(p.Services["GameViewerService"].Start==2&&p.Services["GameViewerService"].Running,"original running UU service not restored");Check(p.Services["AweSunService"].Start==3&&p.Services["ToDesk_Service"].Start==3&&store.State.Phase=="Restored","service configuration not recovered after process close failure");Check(p.Reboots==0,"process close failure caused reboot");});
            test("failed process close leaves originally stopped services stopped",()=>{var p=new FakePlatform {ProcessCloseFailure=true};p.Services["GameViewerService"].Running=false;var e=new SwitchEngine(p,new MemoryStore());Throws(()=>e.Protect(),"process close failure missing");Check(!p.Calls.Any(c=>c.StartsWith("run:"))&&Catalog.AppServices.Keys.All(n=>!p.Services[n].Running),"process close failure started an originally stopped service");Check(p.Items.All(d=>!d.Disabled)&&p.Reboots==0,"process close failure disabled devices or rebooted");});
            test("process close failure during protected reapply preserves existing game mode",()=>{var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();p.Calls.Clear();p.ProcessCloseFailure=true;Throws(()=>e.Protect(),"reapplied process close failure ignored");Check(store.State.Phase=="Protected"&&p.Services.Values.All(s=>s.Start==4)&&p.Items.All(d=>d.Disabled),"failed process-only preflight restored an existing game configuration");Check(!p.Calls.Any(c=>c.StartsWith("disable:")||c.StartsWith("enable:")||c.StartsWith("run:")||Catalog.DriverNames.Any(n=>c.StartsWith("start:"+n+":"))),"reapply process close failure changed driver/device/runtime configuration");Check(p.Reboots==0,"failed protected reapply rebooted");});
            test("process close failure with vanished virtual child restores only app services",()=>{var p=new FakePlatform {ProcessCloseFailure=true,ProcessCloseHideTarget="SIM\\uu-filter"};var store=new MemoryStore();var e=new SwitchEngine(p,store);Throws(()=>e.Protect(),"failed close with vanished child accepted");Check(store.State.Phase=="Restored"&&store.State.Devices.Any(d=>d.InstanceId=="SIM\\uu-filter"),"pre-driver process failure lost baseline or entered unnecessary device recovery");Check(p.Services["GameViewerService"].Running&&p.Services["GameViewerService"].Start==2,"vanished child prevented original service rollback");Check(!p.Calls.Any(c=>c.StartsWith("enable:")||c.StartsWith("disable:")||Catalog.DriverNames.Any(n=>c.StartsWith("start:"+n+":"))),"app-only failure rollback touched devices or drivers");Check(p.Reboots==0,"vanished child failure rebooted");});
            test("process closing repeats on protect but never during restoration",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());e.Protect();e.Protect();Check(p.Calls.Count(c=>c=="close-processes")==2,"reapplied game mode did not check for relaunched remote processes");p.Calls.Clear();e.Restore();Check(!p.Calls.Contains("close-processes")&&p.Services["GameViewerService"].Running,"restore closed remote processes after restoring them");});
            test("child disabled before parent and parent restored first",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());e.Protect();Check(p.Calls.IndexOf("disable:SIM\\uu-filter")<p.Calls.IndexOf("disable:SIM\\uu-hid"),"disable order");p.Calls.Clear();e.Restore();Check(p.Calls.IndexOf("enable:SIM\\uu-hid")<p.Calls.IndexOf("enable:SIM\\uu-filter"),"restore order");});
            test("repeated protect retains original starts",()=>{var p=new FakePlatform();var s=new MemoryStore();var e=new SwitchEngine(p,s);e.Protect();e.Protect();Check(s.State.Drivers.Single(d=>d.Name=="OrayVGC").Start==1,"original overwritten");e.Restore();Check(p.Services["OrayVGC"].Start==1,"restore lost original");});
            test("device failure rolls back and never reboots",()=>{var p=new FakePlatform {FailOnDevice="SIM\\oray-usb"};var s=new MemoryStore();var e=new SwitchEngine(p,s);Throws(()=>e.Protect(),"failure missing");Check(p.Items.All(d=>!d.Disabled),"rollback left a disabled device");Check(p.Services["GameViewerService"].Start==2&&p.Services["GameViewerService"].Running,"app state not rolled back");Check(s.State.Phase=="Restored"&&p.Reboots==0,"failure rebooted or lost backup");});
            test("verified rollback can retry game mode without an extra reboot",()=>{var p=new FakePlatform {FailOnDevice="SIM\\oray-usb"};var store=new MemoryStore();var e=new SwitchEngine(p,store);Throws(()=>e.Protect(),"initial device failure missing");var prior=store.Load();Check(prior.Phase=="Restored"&&e.CanProtect(prior,ScanResult.Capture(p)),"verified original runtime after rollback did not allow retry");p.Calls.Clear();var snapshot=e.Protect();Check(snapshot.Phase=="Protected"&&p.Items.All(d=>d.Disabled),"game retry after complete rollback failed");Check(p.Reboots==0,"verified rollback retry caused implicit reboot");});
            test("same-boot retry rejects mismatched original driver runtime",()=>{var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var restored=e.Restore();p.Services["uuycinput"].Running=false;p.Calls.Clear();Check(!e.CanProtect(restored,ScanResult.Capture(p)),"different driver runtime accepted as fully restored");Throws(()=>e.Protect(),"unsafe same-boot retry accepted");Check(p.Calls.Count==0,"blocked runtime retry changed configuration");});
            test("same-boot retry rejects an outstanding device restart",()=>{var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var restored=e.Restore();p.Items.Single(d=>d.InstanceId=="SIM\\uu-hid").RebootRequired=true;p.Calls.Clear();Check(!e.CanProtect(restored,ScanResult.Capture(p)),"pending device restart accepted as fully restored");Throws(()=>e.Protect(),"pending restart retry accepted");Check(p.Calls.Count==0,"pending restart retry changed configuration");});
            test("same-boot restored runtime equal to baseline permits reapply",()=>{var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var restored=e.Restore();Check(e.CanProtect(restored,ScanResult.Capture(p)),"identical restored device and service runtime incorrectly blocked");e.Protect();Check(store.State.Phase=="Protected"&&p.Services["GameViewerService"].Start==4,"reapply after verified restoration failed");});
            test("snapshot write failure prevents system changes",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore {FailSave=true});Throws(()=>e.Protect(),"save should fail");Check(p.Calls.Count==0,"mutation before durable snapshot");});
            test("foreign machine backup rejected",()=>{var p=new FakePlatform();var s=new MemoryStore();var e=new SwitchEngine(p,s);e.Protect();p.Calls.Clear();s.State.MachineId="OTHER";Throws(()=>e.Restore(),"foreign backup accepted");Check(p.Calls.Count==0,"mutated for foreign backup");});
            test("changed hardware identity rejected before restoration",()=>{var p=new FakePlatform();var s=new MemoryStore();var e=new SwitchEngine(p,s);e.Protect();p.Calls.Clear();p.Items[0].HardwareIds=new[]{"physical\\mouse"};Throws(()=>e.Restore(),"identity change accepted");Check(p.Calls.Count==0,"changed unrelated device");});
            test("timeout does not race automatic rollback",()=>{var p=new FakePlatform {FailOnDevice="SIM\\uu-filter",Pending=true};var s=new MemoryStore();var e=new SwitchEngine(p,s);Throws(()=>e.Protect(),"timeout missing");Check(s.State.Phase=="RecoveryRequired","recovery journal missing");Check(!p.Calls.Any(c=>c.StartsWith("enable:")),"rollback raced pending operation");});
            test("ongoing PnP operation blocks all mutations",()=>{var p=new FakePlatform {Busy=true};var e=new SwitchEngine(p,new MemoryStore());Throws(()=>e.Protect(),"busy not blocked");Check(p.Calls.Count==0,"busy operation mutated system");});
            test("missing restore target retains recoverable journal",()=>{var p=new FakePlatform();var s=new MemoryStore();var e=new SwitchEngine(p,s);e.Protect();p.Items.RemoveAll(d=>d.InstanceId=="SIM\\td-audio");Throws(()=>e.Restore(),"missing target ignored");Check(s.State.Phase=="RecoveryRequired","backup discarded on partial restoration");Check(p.Services["GameViewerService"].Start==4,"apps resumed before complete device restoration");});
            test("restore does not overwrite previous disabled service",()=>{var p=new FakePlatform();p.Services["OrayVGC"].Start=4;var e=new SwitchEngine(p,new MemoryStore());e.Protect();e.Restore();Check(p.Services["OrayVGC"].Start==4,"previous service disable lost");});
            test("restore activates unloaded driver-only UU parent before recreating child",()=>{MemoryStore store;SwitchEngine engine;var p=OfflineProtectedUu(out store,out engine);Check(!p.Devices().Any(d=>d.InstanceId=="SIM\\uu-filter"),"protected reboot did not hide unavailable virtual child");var restored=engine.Restore();Check(p.Calls.Contains("enable:SIM\\uu-hid"),"enabled but unloaded driver-only parent was never reactivated");Check(p.Calls.IndexOf("enable:SIM\\uu-hid")<p.Calls.IndexOf("enable:SIM\\uu-filter"),"virtual child enabled before parent driver was reactivated");Check(restored.Phase=="Restored"&&restored.PendingDevices.Count==0,"synchronously reactivated UU restoration incorrectly left pending work");Check(p.Services["uuycinput"].Running&&p.Services["uuycinputmf"].Running&&p.Services["GameViewerService"].Running,"restored parent, filter or original service remains stopped");Check(p.Items.All(d=>!d.Disabled)&&p.Reboots==0,"normal restore left disabled devices or rebooted implicitly");});
            test("explicit pending parent defers missing child and dependent service until reboot",()=>{MemoryStore store;SwitchEngine engine;var p=OfflineProtectedUu(out store,out engine);p.DeferredEnableTargets.Add("SIM\\uu-hid");p.FailAppStartUntilDriverLoaded=true;var original=store.Load();var restored=engine.Restore();Check(restored.Phase=="Restored","accepted native reboot requirement was reported as failed restore");Check(restored.PendingDevices.Contains("SIM\\uu-hid")&&restored.PendingDevices.Contains("SIM\\uu-filter"),"pending parent or unavailable dependent child was lost");Check(restored.CreatedUtc==original.CreatedUtc&&restored.Devices.Count==original.Devices.Count&&restored.Devices.All(d=>!d.OriginallyDisabled),"pending restore replaced the original baseline");Check(p.Services["uuycinput"].Start==3&&p.Services["OrayVGC"].Start==1&&p.Services["GameViewerService"].Start==2,"pending restore failed to persist original driver/service start values");Check(!p.Services["uuycinput"].Running&&!p.Services["GameViewerService"].Running,"pending driver or dependent service incorrectly reported as running");Check(p.Reboots==0&&!p.Calls.Contains("close-processes"),"restore pending path closed clients or rebooted implicitly");});
            test("postboot continuation restores pending child and verifies original runtime",()=>{MemoryStore store;SwitchEngine engine;var p=OfflineProtectedUu(out store,out engine);p.DeferredEnableTargets.Add("SIM\\uu-hid");p.FailAppStartUntilDriverLoaded=true;var waiting=engine.Restore();string baseline=waiting.CreatedUtc;string restoreBoot=waiting.AppliedBoot;p.BootId="boot-3";p.DeferredEnableTargets.Clear();var root=p.Items.Single(d=>d.InstanceId=="SIM\\uu-hid");root.Started=true;root.Problem=0;root.RawStatus|=8U;root.RebootRequired=false;p.Services["uuycinput"].Running=true;p.Services["GameViewerService"].Running=true;p.Calls.Clear();var completed=engine.ContinueRestoreAfterReboot();Check(completed.Phase=="Restored"&&completed.PendingDevices.Count==0,"postboot pending restoration did not finish");Check(completed.CreatedUtc==baseline&&completed.AppliedBoot==restoreBoot,"continuation lost baseline or hid the actual completed reboot");Check(p.Calls.Contains("enable:SIM\\uu-filter")&&!p.Items.Single(d=>d.InstanceId=="SIM\\uu-filter").Disabled,"postboot virtual child configuration was never restored");Check(engine.ConfigurationDrift(completed).Count==0,"postboot restoration did not match the original active runtime");Check(p.Reboots==0,"continuation rebooted a second time implicitly");});
            test("pending reboot does not mask an unrelated device restoration failure",()=>{MemoryStore store;SwitchEngine engine;var p=OfflineProtectedUu(out store,out engine);p.DeferredEnableTargets.Add("SIM\\uu-hid");p.FailOnDevice="SIM\\oray-usb";p.FailOnce=true;Throws(()=>engine.Restore(),"unrelated device restoration failure was converted to pending success");Check(store.State.Phase=="RecoveryRequired","failed independent restore discarded recovery state");Check(store.State.Devices.Count==7&&store.State.Drivers.Single(d=>d.Name=="uuycinput").Start==3,"failed independent restore lost original backup");Check(p.Reboots==0,"failed independent restore rebooted");});
            test("postboot continuation rejects foreign pending device identities",()=>{MemoryStore store;SwitchEngine engine;var p=OfflineProtectedUu(out store,out engine);p.DeferredEnableTargets.Add("SIM\\uu-hid");engine.Restore();store.State.PendingDevices.Add("USB\\UNRELATED-PHYSICAL-DEVICE");p.BootId="boot-3";p.Calls.Clear();Throws(()=>engine.ContinueRestoreAfterReboot(),"foreign pending identity was accepted for automatic continuation");Check(p.Calls.Count==0&&p.Reboots==0,"foreign pending identity caused mutation or reboot");});
            test("pending restore cannot continue or enter game mode before reboot",()=>{MemoryStore store;SwitchEngine engine;var p=OfflineProtectedUu(out store,out engine);p.DeferredEnableTargets.Add("SIM\\uu-hid");var waiting=engine.Restore();p.Calls.Clear();Check(!engine.CanProtect(waiting,ScanResult.Capture(p)),"pending restore allowed game switch in UI");Throws(()=>engine.ContinueRestoreAfterReboot(),"pending restore continuation ran without an actual reboot");Throws(()=>engine.Protect(),"game mode overwrote pending restore baseline");Check(p.Calls.Count==0&&store.State.PendingDevices.Count>0&&p.Reboots==0,"blocked pending operations mutated state or rebooted");});
            test("service start failure without pending device is a restoration failure",()=>{var p=new FakePlatform();var store=new MemoryStore();var engine=new SwitchEngine(p,store);engine.Protect();p.FailOnAppStart="GameViewerService";Throws(()=>engine.Restore(),"service start failure without explicit pending device was ignored");Check(store.State.Phase=="RecoveryRequired"&&store.State.PendingDevices.Count==0,"unrelated service failure was converted to reboot-pending success");Check(store.State.Apps.Single(a=>a.Name=="GameViewerService").Start==2&&p.Reboots==0,"service failure lost the baseline or rebooted");});
            test("recreated UU mouse child restores by exact identity and managed ancestry",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();
                var child=RecreatedUuFilter(p,"HID\\RECREATED-UU\\COL03");
                var middle=new Device {InstanceId="VHF\\RECREATED-UU\\PARENT",ParentId="SIM\\uu-hid",HardwareIds=new[]{"VHF\\FRAMEWORK"},Provider="Microsoft",Service="vhf"};p.Items.Add(middle);child.ParentId=middle.InstanceId;
                p.Calls.Clear();var restored=e.Restore();var record=restored.Devices.Single(d=>d.TargetId=="uu-filter");
                Check(record.InstanceId=="SIM\\uu-filter"&&record.CurrentInstanceId==child.InstanceId&&SwitchEngine.ActiveId(record)==child.InstanceId,"original identity was overwritten or replacement was not recorded");
                Check(!record.OriginallyDisabled&&record.OriginallyStarted&&!child.Disabled&&child.Started,"recreated child did not restore original baseline");
                Check(p.Calls.Contains("enable:"+child.InstanceId)&&!p.Calls.Contains("enable:SIM\\uu-filter")&&!p.Calls.Any(c=>c.EndsWith(middle.InstanceId)),"restore acted on stale or unmanaged instance");
                Check(e.ConfigurationDrift(restored).Count==0&&e.CanProtect(restored,ScanResult.Capture(p)),"restored alias is not recognized during verification or same-boot retry");
                Check(store.Load().Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId==child.InstanceId,"replacement identity was not persisted");
            });
            test("recreated child under a different parent is not adopted",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var child=RecreatedUuFilter(p,"HID\\WRONG-PARENT\\COL03");child.ParentId="USB\\UNRELATED-PARENT\\1";p.Calls.Clear();
                Throws(()=>e.Restore(),"different-parent child accepted");
                Check(child.Disabled&&!p.Calls.Contains("enable:"+child.InstanceId),"different-parent input device was enabled");Check(string.IsNullOrEmpty(store.Load().Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId),"different parent was persisted as an alias");
            });
            test("ambiguous recreated children are not adopted",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var first=RecreatedUuFilter(p,"HID\\RECREATED-A\\COL03");var second=p.AddProfile("uu-filter","HID\\RECREATED-B\\COL03");second.ParentId="SIM\\uu-hid";second.Disabled=true;second.Started=false;second.Problem=22;second.ConfigFlags=1;p.Calls.Clear();
                Throws(()=>e.Restore(),"ambiguous recreated children accepted");
                Check(first.Disabled&&second.Disabled&&!p.Calls.Contains("enable:"+first.InstanceId)&&!p.Calls.Contains("enable:"+second.InstanceId),"one ambiguous input device was guessed and enabled");Check(string.IsNullOrEmpty(store.Load().Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId),"ambiguous child was persisted");
            });
            test("recreated child with wrong provider or service is not adopted",()=>{
                foreach(bool wrongProvider in new[]{true,false}) {
                    var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var child=RecreatedUuFilter(p,wrongProvider?"HID\\LOOKALIKE-VENDOR\\COL03":"HID\\LOOKALIKE-SERVICE\\COL03");
                    if(wrongProvider)child.Provider="Other Vendor";else child.Service="hidusb";p.Calls.Clear();
                    Throws(()=>e.Restore(),"hardware-only replacement accepted");Check(child.Disabled&&!p.Calls.Contains("enable:"+child.InstanceId),"unverified replacement input was enabled");Check(string.IsNullOrEmpty(store.Load().Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId),"unverified replacement identity persisted");
                }
            });
            test("reapplied protection and drift checks use the current child alias",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var child=RecreatedUuFilter(p,"HID\\CURRENT-UU\\COL03",false);store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId=child.InstanceId;
                Check(e.ConfigurationDrift(store.Load()).Contains(Catalog.Find("uu-filter").Name),"active replacement bypassed game configuration verification");p.Calls.Clear();var protectedState=e.Protect();
                Check(child.Disabled&&p.Calls.Contains("disable:"+child.InstanceId)&&!p.Calls.Contains("disable:SIM\\uu-filter"),"reapplied game mode used stale device identity");Check(protectedState.Devices.Single(d=>d.TargetId=="uu-filter").InstanceId=="SIM\\uu-filter","reapply overwrote original baseline identity");Check(e.ConfigurationDrift(protectedState).Count==0,"protected active alias reported as missing");
            });
            test("missing originally disabled child does not adopt a new active input",()=>{
                var p=new FakePlatform();var original=p.Items.Single(d=>d.InstanceId=="SIM\\uu-filter");original.Disabled=true;original.Started=false;original.ConfigFlags=1;original.Problem=22;var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var child=RecreatedUuFilter(p,"HID\\NEW-ACTIVE-UU\\COL03",false);p.Calls.Clear();var restored=e.Restore();
                Check(restored.Devices.Single(d=>d.TargetId=="uu-filter").OriginallyDisabled&&string.IsNullOrEmpty(restored.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId),"originally-disabled absent child adopted a new identity");Check(!child.Disabled&&!p.Calls.Any(c=>c.EndsWith(child.InstanceId)),"new input was altered for an originally-disabled missing baseline");
            });
            test("an existing child alias with changed hardware is rejected before restoration",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var child=RecreatedUuFilter(p,"HID\\SAVED-ALIAS\\COL03");store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId=child.InstanceId;child.HardwareIds=new[]{"USB\\PHYSICAL-MOUSE"};p.Calls.Clear();
                Throws(()=>e.Restore(),"changed alias hardware identity accepted");Check(p.Calls.Count==0,"alias identity mismatch changed services or devices");
            });
            test("an existing child alias moved to another parent is rejected",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var child=RecreatedUuFilter(p,"HID\\MOVED-ALIAS\\COL03");store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId=child.InstanceId;child.ParentId="USB\\DIFFERENT-PARENT\\1";p.Calls.Clear();
                Check(e.ConfigurationDrift(store.Load()).Contains(Catalog.Find("uu-filter").Name),"changed managed parent was not reported as drift");Throws(()=>e.Restore(),"changed alias parent accepted");Check(p.Calls.Count==0,"changed alias parent triggered system changes");
            });
            test("duplicate active device aliases in a backup are rejected",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId="SIM\\uu-hid";p.Calls.Clear();Throws(()=>e.Restore(),"two records sharing the same active instance were accepted");Check(p.Calls.Count==0,"colliding aliases mutated configuration");
            });
            test("root and display instances are not rebound by profile similarity",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();p.Items.RemoveAll(d=>d.InstanceId=="SIM\\uu-display");var display=p.AddProfile("uu-display","ROOT\\DISPLAY\\REPLACEMENT");display.Disabled=true;display.Started=false;display.Problem=22;display.ConfigFlags=1;p.Calls.Clear();Throws(()=>e.Restore(),"missing root-level display was silently rebound");Check(display.Disabled&&!p.Calls.Contains("enable:"+display.InstanceId),"unrelated root-level display instance was enabled");Check(string.IsNullOrEmpty(store.Load().Devices.Single(d=>d.TargetId=="uu-display").CurrentInstanceId),"root-level device alias was persisted");
            });
            test("an existing child alias with another matching child is rejected",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var active=RecreatedUuFilter(p,"HID\\ACTIVE-ALIAS\\COL03");store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId=active.InstanceId;var duplicate=p.AddProfile("uu-filter","HID\\DUPLICATE-AFTER-ALIAS\\COL03");duplicate.ParentId="SIM\\uu-hid";duplicate.Disabled=true;duplicate.Started=false;p.Calls.Clear();
                Check(e.ConfigurationDrift(store.Load()).Contains(Catalog.Find("uu-filter").Name),"ambiguous persisted alias was not reported");Throws(()=>e.Restore(),"persisted alias silently won over another matching child");Check(p.Calls.Count==0&&active.Disabled&&duplicate.Disabled,"ambiguous persisted alias triggered changes");
            });
            test("reappearing original instance invalidates a saved child alias",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var active=RecreatedUuFilter(p,"HID\\ACTIVE-WITH-ORIGINAL\\COL03");store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId=active.InstanceId;var original=p.AddProfile("uu-filter","SIM\\uu-filter");original.ParentId="SIM\\uu-hid";original.Disabled=true;original.Started=false;p.Calls.Clear();
                Check(e.ConfigurationDrift(store.Load()).Contains(Catalog.Find("uu-filter").Name),"reappearing original instance did not invalidate saved alias");Throws(()=>e.Restore(),"saved alias was trusted while original instance also existed");Check(p.Calls.Count==0&&active.Disabled&&original.Disabled,"original-and-alias ambiguity triggered changes");
            });
            test("changed original identity cannot be bypassed when its saved alias disappears",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var alias=RecreatedUuFilter(p,"HID\\DISAPPEARED-ALIAS\\COL03");store.State.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId=alias.InstanceId;p.Items.Remove(alias);
                var original=p.AddProfile("uu-filter","SIM\\uu-filter");original.ParentId="SIM\\uu-hid";original.HardwareIds=new[]{"USB\\PHYSICAL-MOUSE"};var replacement=p.AddProfile("uu-filter","HID\\OTHER-VALID-CANDIDATE\\COL03");replacement.ParentId="SIM\\uu-hid";replacement.Disabled=true;replacement.Started=false;p.Calls.Clear();
                Throws(()=>e.Restore(),"changed original identity was bypassed by adopting another candidate");Check(replacement.Disabled&&!p.Calls.Contains("enable:"+replacement.InstanceId),"another matching device was enabled despite changed original identity");Check(store.Load().Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId==alias.InstanceId,"changed original identity was hidden by rewriting its saved alias");
            });
            test("restored child reconciliation uses cached scan without system changes",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();var restored=e.Restore();p.BootId="boot-2";var child=RecreatedUuFilter(p,"HID\\POSTBOOT-RECREATED\\COL03",false);var scan=ScanResult.Capture(p);int scans=p.DeviceScans;p.Calls.Clear();
                Check(restored.PendingDevices.Count==0,"fixture still has an outstanding restoration");var reconciled=e.ReconcileRestoredDevices(restored,scan);var record=reconciled.Devices.Single(d=>d.TargetId=="uu-filter");
                Check(record.InstanceId=="SIM\\uu-filter"&&record.CurrentInstanceId==child.InstanceId,"postboot metadata reconciliation lost original or active child identity");Check(store.Load().Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId==child.InstanceId,"reconciled child identity was not persisted");Check(p.Calls.Count==0&&p.DeviceScans==scans,"metadata reconciliation changed system state or enumerated devices again");Check(e.ConfigurationDrift(reconciled,scan).Count==0&&p.DeviceScans==scans,"cached postboot scan falsely reports a healthy recreated restored child");
            });
            test("protected child reconciliation does not adopt a new active input",()=>{
                var p=new FakePlatform();var store=new MemoryStore();var e=new SwitchEngine(p,store);var protectedState=e.Protect();p.BootId="boot-2";foreach(var service in p.Services.Values)service.Running=false;var child=RecreatedUuFilter(p,"HID\\UNEXPECTED-GAME-INPUT\\COL03",false);var scan=ScanResult.Capture(p);int scans=p.DeviceScans;p.Calls.Clear();var reconciled=e.ReconcileRestoredDevices(protectedState,scan);
                Check(reconciled.Phase=="Protected"&&string.IsNullOrEmpty(reconciled.Devices.Single(d=>d.TargetId=="uu-filter").CurrentInstanceId),"game configuration silently adopted a newly active remote input");Check(e.ConfigurationDrift(reconciled,scan).Contains(Catalog.Find("uu-filter").Name),"new active game input was hidden during reconciliation");Check(p.Calls.Count==0&&p.DeviceScans==scans&&!child.Disabled,"protected reconciliation modified devices or performed another scan");
            });
            test("explicit restart request only",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());e.Protect();Check(p.Reboots==0,"implicit reboot");e.Restart();Check(p.Reboots==1,"explicit reboot missing");});
            test("external reenable is visible as configuration drift",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());var s=e.Protect();Check(e.ConfigurationDrift(s).Count==0,"clean game configuration is inconsistent");p.Services["uuycinput"].Start=3;p.Items[0].Disabled=false;Check(e.ConfigurationDrift(s).Count>=2,"external configuration changes not reported");});
            test("driver unexpectedly loaded after reboot is reported",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());var s=e.Protect();p.BootId="boot-2";Check(e.ConfigurationDrift(s).Contains("uuycinput"),"loaded driver was reported as protected after reboot");});
            test("non-disableable UU parent uses dedicated driver reboot plan",()=>{var p=new FakePlatform();var root=p.Items.Single(d=>d.InstanceId=="SIM\\uu-hid");root.RawStatus=0x0180000B;var store=new MemoryStore();var e=new SwitchEngine(p,store);var snapshot=e.Protect();Check(snapshot.Devices.Single(d=>d.InstanceId==root.InstanceId).DriverOnly,"non-disableable parent was not planned for dedicated-driver shutdown");Check(!p.Calls.Contains("disable:"+root.InstanceId)&&!root.Disabled,"non-disableable parent was forced through live disable");Check(p.Services["uuycinput"].Start==4&&p.Services["uuycinput"].Running,"driver shutdown must remain pending until reboot");Check(e.ConfigurationDrift(snapshot).Count==0,"valid pre-reboot pending configuration reported as failure");Check(p.Reboots==0,"driver reboot plan triggered an implicit reboot");});
            test("driver-only reboot plan verifies unloaded driver and stopped device",()=>{var p=new FakePlatform();var root=p.Items.Single(d=>d.InstanceId=="SIM\\uu-hid");root.RawStatus=0x0180000B;var e=new SwitchEngine(p,new MemoryStore());var snapshot=e.Protect();p.BootId="boot-2";foreach(var service in p.Services.Values)service.Running=false;root.Started=false;root.RawStatus&=~8U;root.Problem=32;Check(!root.Disabled&&e.ConfigurationDrift(snapshot).Count==0,"unloaded disabled-service parent wrongly requires a disabled PnP node");p.Services["uuycinput"].Running=true;Check(e.ConfigurationDrift(snapshot).Contains("uuycinput"),"driver-only plan hid a driver still loaded after reboot");p.Services["uuycinput"].Running=false;root.Started=true;root.RawStatus|=8U;Check(e.ConfigurationDrift(snapshot).Contains(Catalog.Find("uu-hid").Name),"driver-only plan hid a still-started parent after reboot");});
            test("driver-only restoration preserves enabled parent and original driver start",()=>{var p=new FakePlatform();var root=p.Items.Single(d=>d.InstanceId=="SIM\\uu-hid");root.RawStatus=0x0180000B;var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();p.Calls.Clear();var restored=e.Restore();Check(p.Services["uuycinput"].Start==3&&!root.Disabled,"driver-only parent baseline not restored");Check(!p.Calls.Contains("enable:"+root.InstanceId)&&!p.Calls.Contains("disable:"+root.InstanceId),"already enabled non-disableable parent received unnecessary live state change");Check(restored.Phase=="Restored","driver-only restore did not finish");});
            test("shared system driver cannot be used for driver-only fallback",()=>{var p=new FakePlatform();var display=p.Items.Single(d=>d.InstanceId=="SIM\\uu-display");display.RawStatus=0x0180000B;var store=new MemoryStore();new SwitchEngine(p,store).Protect();Check(!store.State.Devices.Single(d=>d.InstanceId==display.InstanceId).DriverOnly,"shared driver display incorrectly accepted dedicated-driver fallback");Check(p.Calls.Contains("disable:"+display.InstanceId)&&!p.Calls.Any(c=>c.StartsWith("start:WUDFRd:")),"shared system driver modified instead of device-level operation");});
            test("driver-only backup rejects missing dedicated driver evidence",()=>{var p=new FakePlatform();var root=p.Items.Single(d=>d.InstanceId=="SIM\\uu-hid");root.RawStatus=0x0180000B;var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();store.State.Drivers.RemoveAll(d=>d.Name=="uuycinput");p.Calls.Clear();Throws(()=>e.Restore(),"driver-only backup without its dedicated driver accepted");Check(p.Calls.Count==0,"invalid driver-only backup mutated system");});
            test("new active remote instance after protection is reported",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());var snapshot=e.Protect();p.BootId="boot-2";foreach(var service in p.Services.Values)service.Running=false;p.Items.RemoveAll(d=>d.InstanceId=="SIM\\uu-display");p.AddProfile("uu-display","ROOT\\DISPLAY\\NEW-INSTANCE");Check(e.ConfigurationDrift(snapshot).Any(d=>d.Contains(Catalog.Find("uu-display").Name)),"new active virtual display instance bypassed post-reboot verification");});
            test("restore starts original app before waiting for virtual mouse child",()=>{var p=new FakePlatform {HideFilterUntilServiceRuns=true};var e=new SwitchEngine(p,new MemoryStore());e.Protect();e.Restore();Check(p.Services["GameViewerService"].Running,"original service not running");Check(p.Items.All(d=>!d.Disabled),"virtual child restoration incomplete");});
            test("startup inventory enumerates once and makes no changes",()=>{var p=new FakePlatform();var scan=ScanResult.Capture(p);Check(p.DeviceScans==1&&p.SoftwareScans==1,"scan performed duplicate device or software enumeration");Check(scan.Devices.Count==7&&scan.Software.Count==3,"scan lost installed components");Check(scan.Services.Count==p.Services.Count&&scan.BootId==p.BootId,"scan omitted service or boot status");Check(p.Calls.Count==0&&p.Reboots==0,"inventory mutated the system");});
            test("remote process roots reject broad or relative installation paths",()=>{Check(RemoteProcessRules.NormalizeRoot("D:\\")==null&&RemoteProcessRules.NormalizeRoot("relative\\UU")==null&&RemoteProcessRules.NormalizeRoot(Environment.SystemDirectory)==null,"broad or relative path accepted as a remote install root");Check(RemoteProcessRules.NormalizeRoot("E:\\Tools\\UU Remote\\")!=null,"portable dedicated installation directory rejected");});
            test("remote process path checks enforce directory boundaries",()=>{const string root="D:\\Apps\\ToDesk";Check(RemoteProcessRules.IsUnderRoot(root+"\\ToDesk.exe",root),"normal executable under installation root rejected");Check(!RemoteProcessRules.IsUnderRoot("D:\\Apps\\ToDeskBackup\\ToDesk.exe",root)&&!RemoteProcessRules.IsUnderRoot(root+"\\..\\Other\\ToDesk.exe",root),"sibling or parent traversal path escaped installation boundary");});
            test("remote process closing requires matching installation root and file identity",()=>{var roots=new[]{new RemoteInstallRoot {ProductId="uu",Path="E:\\Remote Apps\\UU"}};var valid=new RemoteProcessInfo {Id=42,ProductId="uu",Name="GameViewer",ExecutablePath="E:\\Remote Apps\\UU\\GameViewer.exe",StartTimeUtc="2026-11-05T12:00:00.0000000Z",Company="Netease",ProductName="GameViewer",OriginalFileName="GameViewer.exe"};Check(RemoteProcessRules.CanClose(valid,roots),"verified vendor binary at portable install path rejected");valid.ExecutablePath="E:\\Other Apps\\GameViewer.exe";Check(!RemoteProcessRules.CanClose(valid,roots),"same-name process outside installation root accepted");valid.ExecutablePath="E:\\Remote Apps\\UU\\Notes.exe";valid.Name="Notes";valid.OriginalFileName="Notes.exe";valid.Company="Other Vendor";valid.ProductName="Text Editor";Check(!RemoteProcessRules.CanClose(valid,roots),"unrelated binary inside installation directory accepted");valid.ProductId="oray";valid.Name="AweSun";valid.ExecutablePath="E:\\Remote Apps\\UU\\AweSun.exe";valid.Company="Oray";valid.ProductName="AweSun";Check(!RemoteProcessRules.CanClose(valid,roots),"another product reused a trusted installation root");});
            test("remote process identity prevents PID reuse and path replacement",()=>{var expected=new RemoteProcessInfo {Id=42,ProductId="uu",ExecutablePath="E:\\Sample Apps\\UU\\GameViewer.exe",StartTimeUtc="2026-11-05T12:00:00.0000000Z"};var current=new RemoteProcessInfo {Id=42,ProductId="uu",ExecutablePath="E:\\Sample Apps\\UU\\GameViewer.exe",StartTimeUtc=expected.StartTimeUtc};Check(RemoteProcessRules.SameIdentity(expected,current),"same remote process identity rejected");current.StartTimeUtc="2026-11-05T12:01:00.0000000Z";Check(!RemoteProcessRules.SameIdentity(expected,current),"reused PID matched old process identity");current.StartTimeUtc=expected.StartTimeUtc;current.ExecutablePath="D:\\Other\\GameViewer.exe";Check(!RemoteProcessRules.SameIdentity(expected,current),"changed process path accepted");current.ExecutablePath=expected.ExecutablePath;current.ProductId="td";Check(!RemoteProcessRules.SameIdentity(expected,current)&&!RemoteProcessRules.SameIdentity(expected,null),"changed product or missing process accepted");});
            test("process handle and window races are tolerated after exit",()=>{
                var command=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/d /c exit 0") {CreateNoWindow=true,UseShellExecute=false};
                using(var child=Process.Start(command)) {
                    Check(child.WaitForExit(5000),"test command did not exit");
                    Check(RemoteProcessController.InvokeWhileRunning<RemoteProcessInfo>(child,()=>{throw new InvalidOperationException("process already exited");})==null,"exited process produced a usable identity");
                    Check(!RemoteProcessController.InvokeWhileRunning<bool>(child,()=>{throw new Win32Exception(6);}),"exited process reported a successful window action");
                }
            });
            test("process operation failures are reported while the process remains alive",()=>{
                using(var current=Process.GetCurrentProcess()) {
                    var failure=new Win32Exception(5);
                    try {RemoteProcessController.InvokeWhileRunning<bool>(current,()=>{throw failure;});}
                    catch(Win32Exception actual){Check(actual==failure,"original process error was replaced");return;}
                    throw new Exception("access failure for a live process was ignored");
                }
            });
            test("remote helper allowlist stays scoped and excludes maintenance",()=>{var roots=new[]{new RemoteInstallRoot {ProductId="uu",Path="E:\\Sample Apps\\UU"}};var process=new RemoteProcessInfo {Id=73,ProductId="uu",ExecutablePath="E:\\Sample Apps\\UU\\bin\\crashpad_handler.exe",StartTimeUtc="2026-11-05T12:00:00.0000000Z",Company="Chromium",ProductName="Crashpad"};Check(RemoteProcessRules.CanClose(process,roots),"known bundled helper rejected");process.ExecutablePath="D:\\Chrome\\bin\\crashpad_handler.exe";Check(!RemoteProcessRules.CanClose(process,roots),"another application's same-name helper accepted");process.ExecutablePath="E:\\Sample Apps\\UU\\updater.exe";process.Company="Netease";process.ProductName="GameViewer";Check(!RemoteProcessRules.CanClose(process,roots),"maintenance binary accepted for automatic termination");process.ExecutablePath="E:\\Sample Apps\\UU\\GameViewer.exe";process.StartTimeUtc=null;Check(!RemoteProcessRules.CanClose(process,roots),"unknown process creation identity accepted for termination");var current=new RemoteProcessInfo {Id=process.Id,ProductId=process.ProductId,ExecutablePath=process.ExecutablePath};Check(!RemoteProcessRules.SameIdentity(process,current),"two missing creation timestamps treated as same process");});
            test("portable paths and arbitrary device instances restore on another machine",()=>{var p=new FakePlatform {MachineKey="SECOND-COMPUTER"};foreach(var d in p.Items){d.InstanceId="ROOT\\PORTABLE\\"+Guid.NewGuid().ToString("N");d.ParentId=null;}p.AddApp("GameViewerService","E:\\远程软件\\UU Remote\\GameViewerService.exe",2,true);p.AddApp("ToDesk_Service","F:\\Portable Tools\\ToDesk.exe",3);var store=new MemoryStore();var e=new SwitchEngine(p,store);var scan=ScanResult.Capture(p);Check(scan.Software.Single(s=>s.Id=="uu").InstallPath=="E:\\远程软件\\UU Remote","scan hardcoded installation path");e.Protect();Check(store.State.MachineId=="SECOND-COMPUTER"&&store.State.Devices.All(d=>d.InstanceId.StartsWith("ROOT\\PORTABLE\\")),"backup copied machine-specific identity from development PC");e.Restore();Check(p.Items.All(d=>!d.Disabled)&&p.Services["GameViewerService"].ImagePath.Contains("E:\\远程软件"),"portable baseline failed restoration");});
            test("single-product machine switches only installed profiles",()=>{var p=new FakePlatform();p.Items.Clear();p.Services.Clear();p.AddProfile("td-vhidex","ROOT\\TODESKEX\\0008");p.AddProfile("td-filter","HID\\TODESK\\NEW-COLLECTION").ParentId="ROOT\\TODESKEX\\0008";p.AddApp("ToDesk_Service","G:\\Apps\\ToDesk.exe",2,true);var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();Check(store.State.Devices.Count==2&&store.State.Apps.Count==1&&store.State.Drivers.Count==1,"invented uninstalled remote profiles");Check(p.Calls.All(c=>!c.Contains("GameViewer")&&!c.Contains("Oray")),"changed another product");e.Restore();Check(p.Items.All(d=>!d.Disabled)&&p.Services["ToDeskVhidEnhance"].Start==3,"new ToDesk profile did not restore");});
            test("all adapted profiles and shared driver restore once",()=>{var p=new FakePlatform();foreach(var t in Catalog.Targets.Where(t=>!FakePlatform.DefaultTargetIds.Contains(t.Id)))p.AddProfile(t.Id);var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();Check(store.State.Devices.Count==Catalog.Targets.Length,"catalog coverage incomplete");Check(store.State.Drivers.Select(d=>d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()==store.State.Drivers.Count,"shared driver duplicated in backup");Check(p.Calls.Count(c=>c=="start:ToDeskVhci:4")==1&&p.Calls.Contains("start:orayvhidkmdf:4"),"shared or supplementary driver not handled");e.Restore();Check(p.Items.All(d=>!d.Disabled),"expanded catalog left devices disabled");});
            test("vendor-only device is visible but excluded from switching",()=>{var p=new FakePlatform();var unknown=new Device {InstanceId="ROOT\\UNKNOWN-ORAY\\1",Name="Future remote driver",HardwareIds=new[]{"oray\\new-driver"},Provider="Shanghai Best Oray Information Technology Co., Ltd.",Service="FutureOray",Started=true};p.Items.Add(unknown);Check(Catalog.DeviceProduct(unknown)=="oray"&&!Catalog.Targets.Any(t=>t.Matches(unknown)),"unknown vendor device identity classification failed");var store=new MemoryStore();new SwitchEngine(p,store).Protect();Check(!unknown.Disabled&&store.State.Devices.All(d=>d.InstanceId!=unknown.InstanceId),"unknown driver was modified by vendor match alone");});
            test("wrong provider and service exclude lookalike hardware",()=>{var p=new FakePlatform();var template=Catalog.Find("uu-hid");var spoof=new Device {InstanceId="USB\\PHYSICAL\\MOUSE",HardwareIds=new[]{template.HardwareId},Provider="Other Vendor",Service=template.DeviceService};p.Items.Add(spoof);var wrongService=new Device {InstanceId="USB\\PHYSICAL\\OTHER",HardwareIds=new[]{template.HardwareId},Provider=template.Provider,Service="hidusb"};p.Items.Add(wrongService);new SwitchEngine(p,new MemoryStore()).Protect();Check(!spoof.Disabled&&!wrongService.Disabled,"hardware-only match disabled unrelated input device");});
            test("unrelated same-name app service is not paused",()=>{var p=new FakePlatform();p.AddApp("ToDesk_Service","Z:\\OtherApp\\OtherService.exe",2,true);var store=new MemoryStore();new SwitchEngine(p,store).Protect();Check(p.Services["ToDesk_Service"].Start==2&&p.Services["ToDesk_Service"].Running,"same-name unverified service changed");Check(!store.State.Apps.Any(a=>a.Name=="ToDesk_Service"),"unverified service captured");});
            test("hierarchy determines USB child and parent order",()=>{var p=new FakePlatform {HideChildrenWithDisabledParent=true};var root=p.AddProfile("td-usbroot");var controller=p.AddProfile("td-vhci");controller.ParentId=root.InstanceId;var hub=p.AddProfile("td-vhub");hub.ParentId=controller.InstanceId;var e=new SwitchEngine(p,new MemoryStore());e.Protect();Check(p.Calls.IndexOf("disable:"+hub.InstanceId)<p.Calls.IndexOf("disable:"+controller.InstanceId)&&p.Calls.IndexOf("disable:"+controller.InstanceId)<p.Calls.IndexOf("disable:"+root.InstanceId),"virtual USB hierarchy disable order incorrect");p.Calls.Clear();e.Restore();Check(p.Calls.IndexOf("enable:"+root.InstanceId)<p.Calls.IndexOf("enable:"+controller.InstanceId)&&p.Calls.IndexOf("enable:"+controller.InstanceId)<p.Calls.IndexOf("enable:"+hub.InstanceId),"virtual USB hierarchy restore order incorrect");});
            test("managed ancestry crosses unmanaged intermediate PnP nodes",()=>{var p=new FakePlatform {HideChildrenWithDisabledParent=true};p.Items.Clear();p.Services.Clear();var parent=p.AddProfile("td-vhidex");var middle=new Device {InstanceId="HID\\GENERIC-COLLECTION\\1",ParentId=parent.InstanceId,HardwareIds=new[]{"HID\\GENERIC"},Service="hidusb",Provider="Microsoft"};p.Items.Add(middle);var child=p.AddProfile("td-gamepad");child.ParentId=middle.InstanceId;var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();Check(store.State.Devices.Count==2&&store.State.Devices.Single(d=>d.InstanceId==child.InstanceId).ParentId==parent.InstanceId,"managed parent not resolved through intermediate node");Check(p.Calls.IndexOf("disable:"+child.InstanceId)<p.Calls.IndexOf("disable:"+parent.InstanceId),"parent removed child before it was disabled");Check(!middle.Disabled&&!p.Calls.Any(c=>c.EndsWith(middle.InstanceId)),"unmanaged intermediate PnP node modified");e.Restore();Check(!parent.Disabled&&!child.Disabled,"managed descendants failed restoration");});
            test("non-filter virtual child can return after original app starts",()=>{var p=new FakePlatform();var parent=p.AddProfile("td-usbroot");var child=p.AddProfile("td-vhub");child.ParentId=parent.InstanceId;p.Services["ToDesk_Service"].Running=true;p.DeferredUntilAppRuns[child.InstanceId]="ToDesk_Service";var e=new SwitchEngine(p,new MemoryStore());e.Protect();child.Disabled=true;child.Started=false;p.Calls.Clear();e.Restore();Check(!child.Disabled&&p.Services["ToDesk_Service"].Running,"app-created non-filter child was not restored");Check(p.Calls.IndexOf("run:ToDesk_Service")<p.Calls.IndexOf("enable:"+child.InstanceId),"child restoration attempted before service recreated it");});
            test("restore leaves originally stopped applications stopped",()=>{var p=new FakePlatform();p.Services["GameViewerService"].Running=false;var e=new SwitchEngine(p,new MemoryStore());e.Protect();e.Restore();Check(!p.Services.Values.Where(s=>Catalog.AppServices.ContainsKey(s.Name)).Any(s=>s.Running),"restore started an originally stopped application");});
            test("missing originally disabled child does not block restoration",()=>{var p=new FakePlatform();var child=p.Items.Single(d=>d.InstanceId=="SIM\\uu-filter");child.Disabled=true;child.Started=false;var store=new MemoryStore();var e=new SwitchEngine(p,store);e.Protect();p.Items.Remove(child);e.Restore();Check(store.State.Phase=="Restored"&&p.Services["GameViewerService"].Running,"absent originally disabled child blocked baseline restoration");Check(!e.ConfigurationDrift(store.State).Contains(Catalog.Find("uu-filter").Name),"absence of originally disabled child reported as drift");});
            test("restore verification detects enabled device runtime failure after reboot",()=>{var p=new FakePlatform();var e=new SwitchEngine(p,new MemoryStore());e.Protect();var snapshot=e.Restore();p.BootId="boot-2";var device=p.Items.Single(d=>d.InstanceId=="SIM\\uu-display");device.Started=false;device.Problem=10;Check(e.ConfigurationDrift(snapshot).Contains(Catalog.Find("uu-display").Name),"failed restored device incorrectly reported as ready");device.Problem=0;Check(e.ConfigurationDrift(snapshot).Contains(Catalog.Find("uu-display").Name),"formerly started restored device runtime loss ignored");});
            test("restore verification preserves a dormant device baseline",()=>{var p=new FakePlatform();var device=p.Items.Single(d=>d.InstanceId=="SIM\\uu-display");device.Started=false;var e=new SwitchEngine(p,new MemoryStore());e.Protect();var snapshot=e.Restore();p.BootId="boot-2";device.Started=false;device.Problem=0;Check(!e.ConfigurationDrift(snapshot).Contains(Catalog.Find("uu-display").Name),"preexisting dormant device forced into running expectation");});
            test("restore verification respects an originally disabled dedicated driver",()=>{var p=new FakePlatform();p.Services["OrayVGC"].Start=4;var e=new SwitchEngine(p,new MemoryStore());e.Protect();var snapshot=e.Restore();p.BootId="boot-2";var device=p.Items.Single(d=>d.InstanceId=="SIM\\oray-gamepad");device.Started=false;device.Problem=32;Check(!e.ConfigurationDrift(snapshot).Contains(Catalog.Find("oray-gamepad").Name),"original driver-disabled baseline incorrectly requires running device");});
            test("resume task XML uses current-user elevated interactive logon",()=>{const string sid="S-1-5-21-111-222-333-1001";const string executable="C:\\ProgramData\\Remote & Tools\\RemoteDriverSwitch.exe";var xml=new System.Xml.XmlDocument();xml.LoadXml(WindowsResume.BuildTaskXml(sid,executable));var ns=new System.Xml.XmlNamespaceManager(xml.NameTable);ns.AddNamespace("t","http://schemas.microsoft.com/windows/2004/02/mit/task");Func<string,string> value=x=>xml.SelectSingleNode(x,ns).InnerText;Check(value("/t:Task/t:Triggers/t:LogonTrigger/t:UserId")==sid,"resume logon trigger targets wrong user");Check(value("/t:Task/t:Principals/t:Principal/t:UserId")==sid&&value("/t:Task/t:Principals/t:Principal/t:LogonType")=="InteractiveToken"&&value("/t:Task/t:Principals/t:Principal/t:RunLevel")=="HighestAvailable","resume principal cannot show elevated desktop UI");Check(value("/t:Task/t:Actions/t:Exec/t:Command")==executable&&value("/t:Task/t:Actions/t:Exec/t:Arguments")=="--resume","resume action or XML path escaping incorrect");Check(xml.SelectNodes("/t:Task/t:Triggers/*",ns).Count==1&&xml.SelectNodes("//t:Repetition",ns).Count==0&&xml.SelectNodes("/t:Task/t:Actions/*",ns).Count==1,"resume task contains repeated triggers or extra actions");Check(!xml.InnerText.Contains("shutdown.exe"),"verification task reboots automatically");});
            test("resume task rejects invalid user and executable identities",()=>{Throws(()=>WindowsResume.BuildTaskXml("not-a-sid","C:\\ProgramData\\tool.exe"),"invalid SID accepted");Throws(()=>WindowsResume.BuildTaskXml("S-1-5-18","relative\\tool.exe"),"relative elevated task command accepted");Throws(()=>WindowsResume.BuildTaskXml("S-1-5-18","C:\\bad\"name.exe"),"quoted task path accepted");});
            test("resume verification requires a new boot",()=>{var record=new ResumeRecord {BootBefore="boot-1",SnapshotCreated="snapshot-1",ExpectedPhase="Protected"};Check(!WindowsResume.Ready(record,"boot-1")&&!WindowsResume.Ready(record,"")&&!WindowsResume.Ready(null,"boot-2"),"same or unknown boot treated as reboot completion");Check(WindowsResume.Ready(record,"boot-2"),"new boot did not trigger verification");});
            test("resume goal detects changed operation and phase",()=>{var record=new ResumeRecord {BootBefore="boot-1",SnapshotCreated="snapshot-1",ExpectedPhase="Protected"};var snapshot=new Snapshot {CreatedUtc="snapshot-1",Phase="Protected"};Check(WindowsResume.GoalMatches(record,snapshot),"identical resume goal rejected");snapshot.Phase="Restored";Check(!WindowsResume.GoalMatches(record,snapshot),"changed phase treated as same operation");snapshot.Phase="Protected";snapshot.CreatedUtc="snapshot-2";Check(!WindowsResume.GoalMatches(record,snapshot)&&!WindowsResume.GoalMatches(record,null)&&!WindowsResume.GoalMatches(null,snapshot),"changed snapshot treated as same operation");});
            test("device transition requires fresh reboot evidence or explicit reboot exit",()=>{var before=new Device {RebootRequired=false};var after=new Device {RebootRequired=false};Check(!WindowsPlatform.TransitionNeedsReboot(0,before,after)&&!WindowsPlatform.TransitionNeedsReboot(0,before,null),"unchanged or absent device without restart evidence accepted");after.RebootRequired=true;Check(WindowsPlatform.TransitionNeedsReboot(0,before,after),"new Windows restart requirement rejected");before.RebootRequired=true;Check(!WindowsPlatform.TransitionNeedsReboot(0,before,after),"preexisting unrelated restart requirement accepted");Check(!WindowsPlatform.TransitionNeedsReboot(5,new Device(),after),"failed command accepted due to restart flag");Check(WindowsPlatform.TransitionNeedsReboot(3010,before,null),"explicit success-reboot-required rejected");});
            test("native enable distinguishes complete configuration from explicit restart",()=>{Check(NativeDeviceState.EvaluateEnable(true,0,0,true,0,0)==NativeEnableDecision.Completed,"successful global and profile enable was rejected");Check(NativeDeviceState.EvaluateEnable(true,0,0x80,true,0,0)==NativeEnableDecision.RebootRequired,"global restart requirement was ignored");Check(NativeDeviceState.EvaluateEnable(true,0,0,true,0,0x100)==NativeEnableDecision.RebootRequired,"profile reboot requirement was ignored");});
            test("native enable accepts disconnected profile only after persisted reboot evidence",()=>{Check(NativeDeviceState.EvaluateEnable(true,0,0x100,false,1167,0)==NativeEnableDecision.RebootRequired,"accepted global reboot request with disconnected profile was rejected");Check(NativeDeviceState.EvaluateEnable(true,0,0,false,1167,0)==NativeEnableDecision.Failed,"1167 without committed reboot evidence was accepted");Check(NativeDeviceState.EvaluateEnable(true,0,0x100,false,5,0)==NativeEnableDecision.Failed,"access denied was masked by an unrelated pending restart");Check(NativeDeviceState.EvaluateEnable(false,5,0,true,0,0)==NativeEnableDecision.Failed,"failed persistent global enable was treated as complete");});
            test("native enable requires readable installer flags after successful scope",()=>{var result=new NativeEnableResult {GlobalSucceeded=true,LocalSucceeded=true,GlobalFlagsRead=false,LocalFlagsRead=true,GlobalFlagsError=5};Check(result.Decision==NativeEnableDecision.Failed,"unknown successful global flags were treated as a fully verified enable");result.GlobalFlagsRead=true;result.LocalFlagsRead=false;Check(result.Decision==NativeEnableDecision.Failed,"unknown successful profile flags were treated as a fully verified enable");result.LocalFlagsRead=true;Check(result.Decision==NativeEnableDecision.Completed,"fully readable successful scope flags rejected");});
            test("quoted instance IDs reject executable separators",()=>{Throws(()=>WindowsPlatform.Quote("root\" injected"),"quote accepted");Throws(()=>WindowsPlatform.Quote("bad\nvalue"),"newline accepted");Check(WindowsPlatform.Quote("ROOT\\SYSTEM\\0003")=="\"ROOT\\SYSTEM\\0003\"","normal device id rejected");});
            test("atomic file snapshot round trip",()=>{string dir=Path.Combine(Path.GetTempPath(),"RemoteDriverSwitch-test-"+Guid.NewGuid().ToString("N"));var fs=new FileStore(dir,false);var e=new SwitchEngine(new FakePlatform(),fs);e.Protect();Check(fs.Load().Phase=="Protected","saved state unavailable");e.Restore();Check(fs.Load().Phase=="Restored"&&File.Exists(Path.Combine(dir,"state.json.bak")),"atomic backup missing");foreach(string file in Directory.GetFiles(dir))File.Delete(file);Directory.Delete(dir);});
            bool passed=results.All(r=>r.StartsWith("PASS "));
            string report=string.Join(Environment.NewLine,results)+Environment.NewLine+(passed?"ALL PASSED":"FAILED")+Environment.NewLine;
            Console.Write(report);if(!string.IsNullOrEmpty(reportPath))File.WriteAllText(reportPath,report);
            return passed?0:1;
        }
    }
}
