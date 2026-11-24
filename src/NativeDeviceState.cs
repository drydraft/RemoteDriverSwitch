using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RemoteDriverSwitch
{
    public enum NativeEnableDecision {Completed,RebootRequired,Failed}
    public sealed class NativeEnableResult
    {
        public bool GlobalSucceeded,LocalSucceeded,GlobalFlagsRead,LocalFlagsRead;
        public int GlobalError,LocalError,GlobalFlagsError,LocalFlagsError;
        public uint GlobalFlags,LocalFlags;
        public NativeEnableDecision Decision {
            get {
                if((GlobalSucceeded&&!GlobalFlagsRead)||(LocalSucceeded&&!LocalFlagsRead))return NativeEnableDecision.Failed;
                return NativeDeviceState.EvaluateEnable(GlobalSucceeded,GlobalError,GlobalFlags,LocalSucceeded,LocalError,LocalFlags);
            }
        }
        public string Describe()
        {
            return "Global(success="+GlobalSucceeded+", error="+GlobalError+", flags=0x"+GlobalFlags.ToString("X8")+", flagsRead="+GlobalFlagsRead+", flagsError="+GlobalFlagsError+"); CurrentProfile(success="+LocalSucceeded+", error="+LocalError+", flags=0x"+LocalFlags.ToString("X8")+", flagsRead="+LocalFlagsRead+", flagsError="+LocalFlagsError+")";
        }
    }
    public static class NativeDeviceState
    {
        private const uint PropertyChange=0x12,Enable=1,GlobalScope=1,CurrentProfileScope=2,RestartMask=0x180;
        public static NativeEnableDecision EvaluateEnable(bool globalSucceeded,int globalError,uint globalFlags,bool localSucceeded,int localError,uint localFlags)
        {
            if(globalSucceeded&&localSucceeded)return ((globalFlags|localFlags)&RestartMask)!=0?NativeEnableDecision.RebootRequired:NativeEnableDecision.Completed;
            if(globalSucceeded&&!localSucceeded&&localError==1167&&(globalFlags&RestartMask)!=0)return NativeEnableDecision.RebootRequired;
            return NativeEnableDecision.Failed;
        }
        // Mirrors the enable ordering in Microsoft's DevCon sample. The same info-set
        // is retained for both scopes and each operation's installer restart flags.
        public static NativeEnableResult EnableDevice(Device expected,Action<string> trace)
        {
            var target=Catalog.Targets.FirstOrDefault(t=>t.Matches(expected));
            if(target==null)throw new InvalidOperationException("设备身份不在适配清单内，未执行原生启用。");
            IntPtr set=SetupDiCreateDeviceInfoList(IntPtr.Zero,IntPtr.Zero);
            if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法创建设备信息集。");
            try {
                var info=new SP_DEVINFO_DATA {cbSize=(uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA))};
                if(!SetupDiOpenDeviceInfo(set,expected.InstanceId,IntPtr.Zero,0,ref info))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法打开待恢复设备："+expected.InstanceId);
                var current=ReadIdentity(set,ref info);
                if(!string.Equals(current.InstanceId,expected.InstanceId,StringComparison.OrdinalIgnoreCase)||!target.Matches(current))throw new InvalidOperationException("原生启用前设备身份发生变化："+expected.InstanceId);
                var result=new NativeEnableResult();
                Apply(set,ref info,GlobalScope,out result.GlobalSucceeded,out result.GlobalError,out result.GlobalFlagsRead,out result.GlobalFlags,out result.GlobalFlagsError);
                Trace(trace,"原生启用全局范围："+expected.InstanceId+"；成功="+result.GlobalSucceeded+"；错误="+result.GlobalError+"；Flags=0x"+result.GlobalFlags.ToString("X8")+"；标志读取成功="+result.GlobalFlagsRead+"；标志读取错误="+result.GlobalFlagsError);
                // Recheck identity in this exact info-set before the second state change.
                current=ReadIdentity(set,ref info);
                if(!string.Equals(current.InstanceId,expected.InstanceId,StringComparison.OrdinalIgnoreCase)||!target.Matches(current))throw new InvalidOperationException("原生启用期间设备身份发生变化："+expected.InstanceId);
                Apply(set,ref info,CurrentProfileScope,out result.LocalSucceeded,out result.LocalError,out result.LocalFlagsRead,out result.LocalFlags,out result.LocalFlagsError);
                Trace(trace,"原生启用当前配置："+expected.InstanceId+"；"+result.Describe()+"；判定="+result.Decision);
                return result;
            }finally{SetupDiDestroyDeviceInfoList(set);}
        }
        private static void Trace(Action<string> trace,string value){if(trace!=null)try{trace(value);}catch{}}
        private static void Apply(IntPtr set,ref SP_DEVINFO_DATA info,uint scope,out bool success,out int error,out bool flagsRead,out uint flags,out int flagsError)
        {
            var parameters=new SP_PROPCHANGE_PARAMS {ClassInstallHeader=new SP_CLASSINSTALL_HEADER {cbSize=(uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER)),InstallFunction=PropertyChange},StateChange=Enable,Scope=scope,HwProfile=0};
            success=SetupDiSetClassInstallParams(set,ref info,ref parameters,(uint)Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS)));
            if(success)success=SetupDiCallClassInstaller(PropertyChange,set,ref info);
            error=success?0:Marshal.GetLastWin32Error();
            var installation=new SP_DEVINSTALL_PARAMS {cbSize=(uint)Marshal.SizeOf(typeof(SP_DEVINSTALL_PARAMS)),DriverPath=""};
            flagsRead=SetupDiGetDeviceInstallParams(set,ref info,ref installation);
            flagsError=flagsRead?0:Marshal.GetLastWin32Error();flags=flagsRead?installation.Flags:0;
        }
        private static Device ReadIdentity(IntPtr set,ref SP_DEVINFO_DATA info)
        {
            var text=new StringBuilder(4096);uint needed;
            if(!SetupDiGetDeviceInstanceId(set,ref info,text,(uint)text.Capacity,out needed))throw new Win32Exception(Marshal.GetLastWin32Error(),"设备实例身份读取失败。");
            string driver=Property(set,ref info,9),provider="";
            if(driver.Length>0)using(var key=Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\"+driver))if(key!=null)provider=Convert.ToString(key.GetValue("ProviderName"));
            return new Device {InstanceId=text.ToString(),Service=Property(set,ref info,4),Provider=provider,HardwareIds=Property(set,ref info,1).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries)};
        }
        private static string Property(IntPtr set,ref SP_DEVINFO_DATA info,uint property)
        {
            uint type,needed;
            if(!SetupDiGetDeviceRegistryProperty(set,ref info,property,out type,null,0,out needed)) {
                int error=Marshal.GetLastWin32Error();if(error==13||error==2)return "";
                if(error!=122)throw new Win32Exception(error,"原生设备身份属性读取失败。");
            }
            if(needed==0)return "";
            if(needed>1024*1024)throw new InvalidDataException("原生设备身份属性异常。");
            var bytes=new byte[needed];
            if(!SetupDiGetDeviceRegistryProperty(set,ref info,property,out type,bytes,(uint)bytes.Length,out needed))throw new Win32Exception(Marshal.GetLastWin32Error(),"原生设备身份属性在恢复期间变化。");
            return Encoding.Unicode.GetString(bytes,0,(int)Math.Min(needed,(uint)bytes.Length)).TrimEnd('\0');
        }
        [StructLayout(LayoutKind.Sequential)]private struct SP_DEVINFO_DATA {public uint cbSize;public Guid ClassGuid;public uint DevInst;public IntPtr Reserved;}
        [StructLayout(LayoutKind.Sequential)]private struct SP_CLASSINSTALL_HEADER {public uint cbSize,InstallFunction;}
        [StructLayout(LayoutKind.Sequential)]private struct SP_PROPCHANGE_PARAMS {public SP_CLASSINSTALL_HEADER ClassInstallHeader;public uint StateChange,Scope,HwProfile;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct SP_DEVINSTALL_PARAMS
        {
            public uint cbSize,Flags,FlagsEx;public IntPtr hwndParent,InstallMsgHandler,InstallMsgHandlerContext,FileQueue,ClassInstallReserved;public uint Reserved;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string DriverPath;
        }
        [DllImport("setupapi.dll",SetLastError=true)]private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid,IntPtr parent);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiOpenDeviceInfo(IntPtr set,string id,IntPtr parent,uint flags,ref SP_DEVINFO_DATA info);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiGetDeviceInstanceId(IntPtr set,ref SP_DEVINFO_DATA data,StringBuilder id,uint size,out uint needed);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set,ref SP_DEVINFO_DATA data,uint property,out uint type,byte[] buffer,uint size,out uint needed);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiSetClassInstallParams(IntPtr set,ref SP_DEVINFO_DATA data,ref SP_PROPCHANGE_PARAMS parameters,uint size);
        [DllImport("setupapi.dll",SetLastError=true)]private static extern bool SetupDiCallClassInstaller(uint function,IntPtr set,ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetupDiGetDeviceInstallParams(IntPtr set,ref SP_DEVINFO_DATA data,ref SP_DEVINSTALL_PARAMS parameters);
        [DllImport("setupapi.dll")]private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
