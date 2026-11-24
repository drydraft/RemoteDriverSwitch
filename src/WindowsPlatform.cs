using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RemoteDriverSwitch
{
    public sealed class WindowsPlatform : IPlatform
    {
        private const string ServiceRoot="SYSTEM\\CurrentControlSet\\Services\\";
        private readonly Lazy<string> machineId=new Lazy<string>(ReadMachineId,true);
        private readonly Lazy<string> bootId=new Lazy<string>(ReadBootId,true);
        public Action<string> Diagnostic;
        private void Trace(string message){if(Diagnostic!=null)try{Diagnostic(message);}catch{}}
        public void EnsureReady()
        {
            using(var version=Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion")) {
                int build;
                if(version==null||!int.TryParse(Convert.ToString(version.GetValue("CurrentBuildNumber")),out build)||build<19041)
                    throw new InvalidOperationException("设备切换需要 Windows 10 2004（19041）及以上版本或 Windows 11，当前系统未执行任何切换。");
            }
            var processes=Process.GetProcessesByName("pnputil");
            try {if(processes.Length>0)throw new PendingOperationException("系统中仍有 PnPUtil 操作未结束，请等待后重试。");}
            finally {foreach(var process in processes)process.Dispose();}
        }
        public string MachineId {get{return machineId.Value;}}
        public string BootId {get{return bootId.Value;}}
        private static string ReadMachineId()
        {
            using(var key=Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography")) {
                string value=key==null?"":Convert.ToString(key.GetValue("MachineGuid"));
                if(string.IsNullOrWhiteSpace(value))throw new InvalidOperationException("无法读取本机身份，未执行切换。");
                return value;
            }
        }
        private static string ReadBootId()
        {
            using(var query=new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem")) {
                query.Options.Timeout=TimeSpan.FromSeconds(8);
                using(var rows=query.Get())foreach(ManagementObject row in rows)using(row) {
                    string value=Convert.ToString(row["LastBootUpTime"]);
                    if(!string.IsNullOrWhiteSpace(value))return value;
                }
            }
            throw new InvalidOperationException("无法读取系统启动时间。");
        }
        public List<SoftwareInfo> Software(List<Device> devices)
        {
            var services=new Dictionary<string,ServiceState>(StringComparer.OrdinalIgnoreCase);
            foreach(string name in Catalog.AppServices.Keys) {
                var service=Service(name);if(service!=null)services[name]=service;
            }
            return SoftwareInventory.Read(devices,services,null);
        }
        public ServiceState Service(string name)
        {
            using(var key=Registry.LocalMachine.OpenSubKey(ServiceRoot+name)) {
                if(key==null)return null;
                bool running=false;
                try {using(var sc=new ServiceController(name))running=sc.Status==ServiceControllerStatus.Running;}catch(InvalidOperationException){}
                return new ServiceState {Name=name,ImagePath=Convert.ToString(key.GetValue("ImagePath")),Start=Convert.ToInt32(key.GetValue("Start",-1)),Running=running};
            }
        }
        public void SetStart(string name,int value)
        {
            if(!Catalog.AppServices.ContainsKey(name)&&!Catalog.DriverNames.Contains(name,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("目标服务不在固定清单内。");
            if(value<0||value>4)throw new ArgumentOutOfRangeException("value","服务启动类型无效。");
            IntPtr manager=IntPtr.Zero,service=IntPtr.Zero;
            try {
                manager=OpenSCManager(null,null,1); // SC_MANAGER_CONNECT
                if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法连接 Windows 服务管理器。");
                service=OpenService(manager,name,2); // SERVICE_CHANGE_CONFIG
                if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法修改服务配置："+name);
                // Keep SCM's cached configuration and the persisted Start value in sync.
                if(!ChangeServiceConfig(service,0xffffffff,(uint)value,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"服务启动类型修改失败："+name);
            }finally {
                if(service!=IntPtr.Zero)CloseServiceHandle(service);
                if(manager!=IntPtr.Zero)CloseServiceHandle(manager);
            }
        }
        public void StopApp(string name)
        {
            if(!Catalog.AppServices.ContainsKey(name))throw new InvalidOperationException("未知远控服务。");
            using(var service=new ServiceController(name)) {
                if(service.Status==ServiceControllerStatus.Stopped)return;
                if(!service.CanStop)throw new InvalidOperationException("服务暂不能停止，请先退出远控软件："+name);
                service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));
            }
        }
        public void StartApp(string name)
        {
            if(!Catalog.AppServices.ContainsKey(name))throw new InvalidOperationException("未知远控服务。");
            using(var service=new ServiceController(name)) {
                if(service.Status==ServiceControllerStatus.Running)return;
                service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15));
            }
        }
        public void CloseRemoteProcesses()
        {new RemoteProcessController(this,Trace).Close();}
        public void CloseRemoteProcesses(IEnumerable<string> products)
        {new RemoteProcessController(this,Trace).Close(products);}
        public List<StartupEntry> StartupEntries(){return WindowsStartup.Read();}
        public StartupEntry ReadStartup(StartupEntry entry){return WindowsStartup.ReadSlot(entry);}
        public void SetStartup(StartupEntry entry,bool enabled){WindowsStartup.SetEnabled(entry,enabled);}
        public static string Quote(string value)
        {
            if(value==null||value.IndexOfAny(new[]{'"','\r','\n','\0'})>=0||value.EndsWith("\\"))throw new ArgumentException("设备标识包含无效字符。");
            return "\""+value+"\"";
        }
        private static int Run(string file,string args,int timeout)
        {
            using(var process=new Process {StartInfo=new ProcessStartInfo {FileName=file,Arguments=args,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}}) {
                process.Start();
                if(!process.WaitForExit(timeout)) {
                    // A timed-out device operation may still be completing; never race it with rollback.
                    throw new PendingOperationException("设备操作超过等待时间，请等待该操作结束后重新打开工具恢复。");
                }
                return process.ExitCode;
            }
        }
        public void SetDevice(Device device,bool enabled)
        {
            var target=Catalog.Targets.FirstOrDefault(t=>t.Matches(device));
            if(target==null)throw new InvalidOperationException("设备身份不在固定清单内。");
            var before=Devices().FirstOrDefault(d=>string.Equals(d.InstanceId,device.InstanceId,StringComparison.OrdinalIgnoreCase));
            if(before==null||!target.Matches(before))throw new InvalidOperationException("设备已退出枚举或身份已变化："+device.InstanceId);
            Trace("设备切换前："+Describe(before));
            if(enabled){EnableNative(device,before,target);return;}
            string executable=Path.Combine(Environment.SystemDirectory,"pnputil.exe");
            var result=DeviceCommandRunner.Run(executable,(enabled?"/enable-device ":"/disable-device ")+Quote(device.InstanceId),60000,Trace);
            Device current=null;string lastDescription=null;
            for(int attempt=0;attempt<12;attempt++) {
                current=Devices().FirstOrDefault(d=>string.Equals(d.InstanceId,device.InstanceId,StringComparison.OrdinalIgnoreCase));
                if(current!=null&&!target.Matches(current))throw new InvalidOperationException("切换后设备身份发生变化，已停止验证："+device.InstanceId);
                string description=Describe(current);
                if(description!=lastDescription){Trace("设备切换后："+description);lastDescription=description;}
                if(result.ExitCode!=0&&result.ExitCode!=3010)throw new Win32Exception(result.ExitCode,"设备切换失败："+device.Name+"，代码 "+result.ExitCode+"。命令输出已记录日志。");
                if(!enabled&&(current==null||current.Disabled))return;
                if(enabled&&current!=null&&!current.Disabled&&current.Problem==0)return;
                if(TransitionNeedsReboot(result.ExitCode,before,current)){Trace("设备状态切换已接受，Windows 要求重启后生效："+device.InstanceId);return;}
                Thread.Sleep(250);
            }
            string reason=!enabled&&before.RawStatus!=0&&(before.RawStatus&0x2000)==0?"；Windows 未将此设备标记为可在线禁用":"";
            throw new InvalidOperationException("操作结束后设备状态未符合预期："+device.Name+"；返回码 "+result.ExitCode+"；"+Describe(current)+reason+"。命令输出已记录日志。");
        }
        private void EnableNative(Device requested,Device before,Target target)
        {
            requested.RebootRequired=false;
            var result=NativeDeviceState.EnableDevice(before,Trace);
            bool pending=result.Decision==NativeEnableDecision.RebootRequired;
            Device current=null;string lastDescription=null;
            for(int attempt=0;attempt<12;attempt++) {
                current=Devices().FirstOrDefault(d=>string.Equals(d.InstanceId,requested.InstanceId,StringComparison.OrdinalIgnoreCase));
                if(current!=null&&!target.Matches(current))throw new InvalidOperationException("原生启用后设备身份发生变化："+requested.InstanceId);
                string description=Describe(current);if(description!=lastDescription){Trace("原生启用后："+description);lastDescription=description;}
                if(result.Decision==NativeEnableDecision.Failed) {
                    int error=result.LocalError!=0?result.LocalError:result.GlobalError!=0?result.GlobalError:result.LocalFlagsError!=0?result.LocalFlagsError:result.GlobalFlagsError;
                    throw new Win32Exception(error,"原生设备启用未通过："+requested.Name+"；"+result.Describe()+"；"+description+"。阶段及返回码已记录日志。");
                }
                if(pending){requested.RebootRequired=true;Trace("设备启用请求已接受，Windows 安装标志要求重启后生效："+requested.InstanceId);return;}
                if(current!=null&&!current.Disabled&&(current.ConfigFlags&1)==0&&current.Problem==0)return;
                if(current!=null&&!current.Disabled&&(current.ConfigFlags&1)==0&&current.Problem==32) {
                    requested.RebootRequired=true;Trace("启用配置已恢复，当前服务尚未加载，待重启核验："+requested.InstanceId);return;
                }
                if(TransitionNeedsReboot(0,before,current)){requested.RebootRequired=true;Trace("设备启用后出现新的重启要求："+requested.InstanceId);return;}
                System.Threading.Thread.Sleep(250);
            }
            throw new InvalidOperationException("原生启用后设备状态未符合预期："+requested.Name+"；"+Describe(current)+"；"+result.Describe());
        }
        public static bool TransitionNeedsReboot(int exitCode,Device before,Device after)
        {return exitCode==3010||(exitCode==0&&before!=null&&after!=null&&!before.RebootRequired&&after.RebootRequired);}
        private static string Describe(Device device)
        {return device==null?"设备未枚举":device.InstanceId+"；Disabled="+device.Disabled+"；Started="+device.Started+"；RawStatus=0x"+device.RawStatus.ToString("X8")+"；Problem="+device.Problem+"；ConfigFlags=0x"+device.ConfigFlags.ToString("X8")+"；RebootRequired="+device.RebootRequired;}
        public void Reboot()
        {
            // No /f and no OS countdown: unsaved applications retain their normal shutdown handling.
            int result=Run(Path.Combine(Environment.SystemDirectory,"shutdown.exe"),"/r /t 0",10000);
            if(result!=0)throw new Win32Exception(result,"Windows 未接受重启请求。");
        }
        public List<Device> Devices()
        {
            var list=new List<Device>();
            IntPtr set=SetupDiGetClassDevs(IntPtr.Zero,null,IntPtr.Zero,0x2|0x4);
            if(set==new IntPtr(-1))throw new Win32Exception();
            try {
                for(uint i=0;;i++) {
                    var info=new SP_DEVINFO_DATA {cbSize=(uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA))};
                    if(!SetupDiEnumDeviceInfo(set,i,ref info)){if(Marshal.GetLastWin32Error()==259)break;throw new Win32Exception();}
                    var id=new StringBuilder(4096);uint needed;
                    if(!SetupDiGetDeviceInstanceId(set,ref info,id,(uint)id.Capacity,out needed))throw new Win32Exception();
                    string driverKey=Property(set,ref info,9);
                    string provider="",inf="",version="";
                    if(driverKey.Length>0)using(var key=Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\"+driverKey))if(key!=null){provider=Convert.ToString(key.GetValue("ProviderName"));inf=Convert.ToString(key.GetValue("InfPath"));version=Convert.ToString(key.GetValue("DriverVersion"));}
                    uint status,problem;
                    if(CM_Get_DevNode_Status(out status,out problem,info.DevInst,0)!=0){status=0;problem=uint.MaxValue;}
                    int flags=0;using(var key=Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\"+id))if(key!=null)flags=Convert.ToInt32(key.GetValue("ConfigFlags",0));
                    string friendly=Property(set,ref info,12);if(friendly.Length==0)friendly=Property(set,ref info,0);
                    string parentId="";uint parent;
                    if(CM_Get_Parent(out parent,info.DevInst,0)==0) {
                        var parentText=new StringBuilder(4096);
                        if(CM_Get_Device_ID(parent,parentText,(uint)parentText.Capacity,0)==0)parentId=parentText.ToString();
                    }
                    bool rebootRequired=problem==14||(status&0x100)!=0||RebootProperty(set,ref info);
                    list.Add(new Device {InstanceId=id.ToString(),Name=friendly,Service=Property(set,ref info,4),HardwareIds=Property(set,ref info,1).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries),Provider=provider,Inf=inf,DriverVersion=version,ParentId=parentId,Disabled=problem==22||(flags&1)!=0,Started=(status&8)!=0,Problem=problem,RawStatus=status,ConfigFlags=flags,RebootRequired=rebootRequired});
                }
            }finally{SetupDiDestroyDeviceInfoList(set);}
            return list;
        }
        private static bool RebootProperty(IntPtr set,ref SP_DEVINFO_DATA info)
        {
            // DEVPKEY_Device_IsRebootRequired, from Microsoft's devpkey.h, BOOLEAN.
            var key=new DEVPROPKEY {fmtid=new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),pid=16};
            uint type,needed;var bytes=new byte[1];
            return SetupDiGetDeviceProperty(set,ref info,ref key,out type,bytes,1,out needed,0)&&type==0x11&&needed==1&&bytes[0]!=0;
        }
        private static string Property(IntPtr set,ref SP_DEVINFO_DATA info,uint property)
        {
            uint type,needed;
            if(!SetupDiGetDeviceRegistryProperty(set,ref info,property,out type,null,0,out needed)) {
                int error=Marshal.GetLastWin32Error();
                // ERROR_INVALID_DATA means this optional property is not present.
                if(error==13||error==2)return "";
                if(error!=122)throw new Win32Exception(error,"设备属性读取失败。");
            }
            if(needed==0)return "";
            if(needed>1024*1024)throw new InvalidDataException("设备属性数据异常，已停止扫描。");
            byte[] bytes=new byte[needed];
            if(!SetupDiGetDeviceRegistryProperty(set,ref info,property,out type,bytes,(uint)bytes.Length,out needed)) {
                int error=Marshal.GetLastWin32Error();
                if(error==13||error==2)return "";
                throw new Win32Exception(error,"设备属性在扫描期间发生变化，请重新扫描。");
            }
            return Encoding.Unicode.GetString(bytes,0,(int)Math.Min(needed,(uint)bytes.Length)).TrimEnd('\0');
        }
        [StructLayout(LayoutKind.Sequential)]private struct SP_DEVINFO_DATA {public uint cbSize;public Guid ClassGuid;public uint DevInst;public IntPtr Reserved;}
        [StructLayout(LayoutKind.Sequential)]private struct DEVPROPKEY {public Guid fmtid;public uint pid;}
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenService(IntPtr manager,string name,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]private static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint errorControl,string binaryPath,string group,IntPtr tag,string dependencies,string account,string password,string displayName);
        [DllImport("advapi32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr SetupDiGetClassDevs(IntPtr guid,string enumerator,IntPtr parent,uint flags);
        [DllImport("setupapi.dll",SetLastError=true)]private static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiGetDeviceInstanceId(IntPtr set,ref SP_DEVINFO_DATA data,StringBuilder id,uint size,out uint needed);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set,ref SP_DEVINFO_DATA data,uint property,out uint type,byte[] buffer,uint size,out uint needed);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="SetupDiGetDevicePropertyW")]
        private static extern bool SetupDiGetDeviceProperty(IntPtr set,ref SP_DEVINFO_DATA data,ref DEVPROPKEY key,out uint type,byte[] buffer,uint size,out uint needed,uint flags);
        [DllImport("setupapi.dll")]private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")]private static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint devInst,uint flags);
        [DllImport("cfgmgr32.dll")]private static extern uint CM_Get_Parent(out uint parent,uint devInst,uint flags);
        [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode,EntryPoint="CM_Get_Device_IDW")]private static extern uint CM_Get_Device_ID(uint devInst,StringBuilder id,uint size,uint flags);
    }
    public sealed class PendingOperationException : Exception { public PendingOperationException(string text):base(text){} }
}
