using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace RemoteDriverSwitch
{
    public sealed class StartupEntry
    {
        public string Scope, UserSid, Name, Command, ProductId;
        public int View, Kind;

        [System.Web.Script.Serialization.ScriptIgnore]
        public string Identity {get{return Scope+":"+View+":"+UserSid+":"+Name;}}

        public bool Matches(StartupEntry other)
        {
            return other!=null&&StringComparer.OrdinalIgnoreCase.Equals(Identity,other.Identity)
                &&Kind==other.Kind&&StringComparer.Ordinal.Equals(Command,other.Command);
        }
    }

    internal static class WindowsStartup
    {
        private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
        private static readonly Dictionary<string,string> Launchers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"GameViewer.exe","uu"},{"UURemote.exe","uu"},{"UU远程.exe","uu"},
            {"ToDesk.exe","td"},{"ToDesk_ServiceTray.exe","td"},
            {"SunloginClient.exe","oray"},{"AweSun.exe","oray"},{"OldAweSun.exe","oray"}
        };
        private static readonly object Missing=new object();
        private static int NativeView {get{return Environment.Is64BitOperatingSystem?64:32;}}
        private static string CurrentSid()
        {
            using(var identity=WindowsIdentity.GetCurrent())return identity.User.Value;
        }

        private static string StartupExecutable(string command,int kind)
        {
            if(string.IsNullOrWhiteSpace(command)||command.Length>8192||command.IndexOfAny(new[]{'\0','\r','\n'})>=0)return null;
            string text=command.Trim();
            int end;
            string path;
            bool quoted=text[0]=='"';
            if(quoted) {
                end=text.IndexOf('"',1);
                if(end<2||(end+1<text.Length&&!char.IsWhiteSpace(text[end+1])))return null;
                path=text.Substring(1,end-1);
            }else {
                end=text.IndexOfAny(new[]{' ','\t','\r','\n'});
                path=end<0?text:text.Substring(0,end);
            }
            if(kind==(int)RegistryValueKind.ExpandString)path=Environment.ExpandEnvironmentVariables(path);
            if(!quoted&&path.IndexOfAny(new[]{' ','\t'})>=0)return null;
            try {
                if(path.Length<4||!char.IsLetter(path[0])||path[1]!=':'||path[2]!='\\'||path.IndexOf(':',2)>=0)return null;
                string fullPath=Path.GetFullPath(path);
                return RemoteProcessRules.NormalizeRoot(Path.GetDirectoryName(fullPath))==null?null:fullPath;
            }catch(ArgumentException){return null;}
            catch(NotSupportedException){return null;}
            catch(PathTooLongException){return null;}
        }

        private static string LauncherProduct(string executable)
        {
            string product;
            return executable!=null&&Launchers.TryGetValue(Path.GetFileName(executable),out product)?product:null;
        }

        private static bool VendorMatches(string executable,string product)
        {
            if(!File.Exists(executable))return false;
            FileStore.CheckDirectoryPath(Path.GetDirectoryName(executable));
            if((File.GetAttributes(executable)&FileAttributes.ReparsePoint)!=0)return false;
            var description=FileVersionInfo.GetVersionInfo(executable);
            return RemoteProcessRules.IsVendorIdentity(product,description.CompanyName,description.ProductName);
        }

        internal static void Validate(StartupEntry entry)
        {
            if(entry==null||(entry.Scope!="Machine"&&entry.Scope!="User")
                ||(entry.View!=32&&entry.View!=64)
                ||(entry.Scope=="User"&&(entry.View!=NativeView||string.IsNullOrEmpty(entry.UserSid)))
                ||(entry.Scope=="Machine"&&!string.IsNullOrEmpty(entry.UserSid))
                ||string.IsNullOrWhiteSpace(entry.Name)||entry.Name.Length>512||entry.Name.IndexOf('\0')>=0
                ||(entry.Kind!=(int)RegistryValueKind.String&&entry.Kind!=(int)RegistryValueKind.ExpandString)
                ||string.IsNullOrEmpty(entry.ProductId)||LauncherProduct(StartupExecutable(entry.Command,entry.Kind))!=entry.ProductId)
                throw new InvalidDataException("远控登录启动项备份无效。");
            if(entry.Scope=="User")try {new SecurityIdentifier(entry.UserSid);}
            catch(ArgumentException){throw new InvalidDataException("远控登录启动项的用户标识无效。");}
        }

        private static RegistryKey OpenBase(string scope,int view,string user)
        {
            if(scope=="User"&&!string.Equals(user,CurrentSid(),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请用保存启动项时的 Windows 账户登录并运行本工具。");
            return RegistryKey.OpenBaseKey(scope=="User"?RegistryHive.CurrentUser:RegistryHive.LocalMachine,
                view==64?RegistryView.Registry64:RegistryView.Registry32);
        }

        private static StartupEntry ReadValue(RegistryKey key,string scope,int view,string user,string name)
        {
            if(key==null)return null;
            var data=key.GetValue(name,Missing,RegistryValueOptions.DoNotExpandEnvironmentNames);
            if(ReferenceEquals(data,Missing))return null;
            RegistryValueKind kind;
            try{kind=key.GetValueKind(name);}
            catch(IOException) {
                if(ReferenceEquals(key.GetValue(name,Missing,RegistryValueOptions.DoNotExpandEnvironmentNames),Missing))return null;
                throw;
            }
            return new StartupEntry {Scope=scope,View=view,UserSid=user,Name=name,Command=data as string,Kind=(int)kind};
        }

        private static void ReadScope(List<StartupEntry> entries,string scope,int view,string user)
        {
            using(var root=OpenBase(scope,view,user))using(var key=root.OpenSubKey(RunKey)) {
                if(key==null)return;
                foreach(var name in key.GetValueNames()) {
                    if(string.IsNullOrWhiteSpace(name)||name.Length>512)continue;
                    var entry=ReadValue(key,scope,view,user,name);
                    if(entry==null||(entry.Kind!=(int)RegistryValueKind.String&&entry.Kind!=(int)RegistryValueKind.ExpandString))continue;
                    string executable=StartupExecutable(entry.Command,entry.Kind);
                    string product=LauncherProduct(executable);
                    if(product==null||!VendorMatches(executable,product))continue;
                    entry.ProductId=product;entries.Add(entry);
                }
            }
        }

        internal static List<StartupEntry> Read()
        {
            var entries=new List<StartupEntry>();
            // HKCU's Software key is shared on these Windows versions. HKLM's is redirected.
            ReadScope(entries,"User",NativeView,CurrentSid());
            ReadScope(entries,"Machine",NativeView,"");
            if(NativeView==64)ReadScope(entries,"Machine",32,"");
            return entries;
        }

        internal static StartupEntry ReadSlot(StartupEntry expected)
        {
            Validate(expected);
            using(var root=OpenBase(expected.Scope,expected.View,expected.UserSid))using(var key=root.OpenSubKey(RunKey))
                return ReadValue(key,expected.Scope,expected.View,expected.UserSid,expected.Name);
        }

        internal static void SetEnabled(StartupEntry entry,bool enabled)
        {
            Validate(entry);
            using(var root=OpenBase(entry.Scope,entry.View,entry.UserSid))using(var key=root.OpenSubKey(RunKey,true)) {
                var current=ReadValue(key,entry.Scope,entry.View,entry.UserSid,entry.Name);
                if(current!=null&&!entry.Matches(current))
                    throw new IOException("同名登录启动项已变化，请先核对："+entry.Name);
                if(enabled?(current!=null):(current==null))return;
                if(!VendorMatches(StartupExecutable(entry.Command,entry.Kind),entry.ProductId))
                    throw new IOException("无法确认登录启动项仍属于远控软件："+entry.Name);
                if(enabled) {
                    using(var writable=key==null?root.CreateSubKey(RunKey):null) {
                        var destination=key??writable;
                        var latest=ReadValue(destination,entry.Scope,entry.View,entry.UserSid,entry.Name);
                        if(latest!=null&&!entry.Matches(latest))
                            throw new IOException("同名登录启动项已出现，请先核对："+entry.Name);
                        if(latest==null)destination.SetValue(entry.Name,entry.Command,(RegistryValueKind)entry.Kind);
                    }
                }else {
                    var latest=ReadValue(key,entry.Scope,entry.View,entry.UserSid,entry.Name);
                    if(latest!=null&&!entry.Matches(latest))
                        throw new IOException("同名登录启动项已变化，请先核对："+entry.Name);
                    if(latest!=null)key.DeleteValue(entry.Name,false);
                }
            }
            var after=ReadSlot(entry);
            if(enabled?!entry.Matches(after):after!=null)
                throw new IOException("无法核验登录启动项的更改："+entry.Name);
        }
    }
}
