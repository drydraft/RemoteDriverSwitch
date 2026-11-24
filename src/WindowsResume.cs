using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace RemoteDriverSwitch
{
    public sealed class ResumeRecord
    {
        public int Version=1;
        public string MachineId,UserSid,BootBefore,SnapshotCreated,ExpectedPhase,CreatedUtc;
    }
    // Only armed after a successful mode change and an explicit restart choice.
    public sealed class WindowsResume
    {
        private readonly string root;
        private string PendingPath {get{return Path.Combine(root,"resume.json");}}
        private static string UserSid {get{return WindowsIdentity.GetCurrent().User.Value;}}
        public WindowsResume(string root){this.root=root;}
        public static string TaskName(string sid){return "RemoteDriverSwitch-Resume-"+sid;}
        public static string BuildTaskXml(string sid,string executable)
        {
            new SecurityIdentifier(sid);
            if(!Path.IsPathRooted(executable)||executable.IndexOf('"')>=0)throw new ArgumentException("自动启动路径无效。");
            var text=new StringBuilder();
            var settings=new XmlWriterSettings {OmitXmlDeclaration=true,Indent=true};
            using(var xml=XmlWriter.Create(text,settings)) {
                const string ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
                xml.WriteStartElement("Task",ns);xml.WriteAttributeString("version","1.2");
                xml.WriteStartElement("RegistrationInfo");xml.WriteElementString("Description","远控驱动开关：本次重启后扫描核验，完成后移除任务。");xml.WriteEndElement();
                xml.WriteStartElement("Triggers");xml.WriteStartElement("LogonTrigger");xml.WriteElementString("Enabled","true");xml.WriteElementString("UserId",sid);xml.WriteElementString("Delay","PT8S");xml.WriteEndElement();xml.WriteEndElement();
                xml.WriteStartElement("Principals");xml.WriteStartElement("Principal");xml.WriteAttributeString("id","User");xml.WriteElementString("UserId",sid);xml.WriteElementString("LogonType","InteractiveToken");xml.WriteElementString("RunLevel","HighestAvailable");xml.WriteEndElement();xml.WriteEndElement();
                xml.WriteStartElement("Settings");
                xml.WriteElementString("MultipleInstancesPolicy","IgnoreNew");xml.WriteElementString("DisallowStartIfOnBatteries","false");xml.WriteElementString("StopIfGoingOnBatteries","false");xml.WriteElementString("AllowHardTerminate","false");xml.WriteElementString("StartWhenAvailable","true");xml.WriteElementString("RunOnlyIfNetworkAvailable","false");xml.WriteElementString("AllowStartOnDemand","true");xml.WriteElementString("Enabled","true");xml.WriteElementString("Hidden","false");xml.WriteElementString("RunOnlyIfIdle","false");xml.WriteElementString("WakeToRun","false");xml.WriteElementString("ExecutionTimeLimit","PT0S");xml.WriteEndElement();
                xml.WriteStartElement("Actions");xml.WriteAttributeString("Context","User");xml.WriteStartElement("Exec");xml.WriteElementString("Command",executable);xml.WriteElementString("Arguments","--resume");xml.WriteElementString("WorkingDirectory",Path.GetDirectoryName(executable));xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndElement();
            }
            return text.ToString();
        }
        private static void Release(object value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
        private static void Register(string sid,string xml)
        {
            object scheduler=null,folder=null,registered=null;
            try {
                scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
                dynamic service=scheduler;service.Connect();folder=service.GetFolder("\\");dynamic tasks=folder;
                registered=tasks.RegisterTask(TaskName(sid),xml,6,null,null,3,null);
            }finally {Release(registered);Release(folder);Release(scheduler);}
        }
        private static void Remove(string sid)
        {
            object scheduler=null,folder=null;
            try {
                scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
                dynamic service=scheduler;service.Connect();folder=service.GetFolder("\\");dynamic tasks=folder;
                try{tasks.DeleteTask(TaskName(sid),0);}catch(COMException error){if(error.ErrorCode!=unchecked((int)0x80070002))throw;}
            }finally {Release(folder);Release(scheduler);}
        }
        private static void WriteJson(string path,object record)
        {
            byte[] data=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(record));string temp=path+".tmp";
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){stream.Write(data,0,data.Length);stream.Flush(true);}
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        public void Prepare(IPlatform platform,Snapshot snapshot,string sourceExecutable)
        {
            if(snapshot==null||(snapshot.Phase!="Protected"&&snapshot.Phase!="Restored"))throw new InvalidOperationException("当前配置尚未切换完成，不能安排自动启动核验。");
            FileStore.CheckDirectoryPath(root);
            string appDirectory=Path.Combine(root,"app");FileStore.CheckDirectoryPath(appDirectory);Directory.CreateDirectory(appDirectory);FileStore.SecureDirectory(appDirectory);
            string pinned=Path.Combine(appDirectory,"RemoteDriverSwitch.exe");
            if(!string.Equals(Path.GetFullPath(sourceExecutable),Path.GetFullPath(pinned),StringComparison.OrdinalIgnoreCase)) {
                string stage=pinned+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.Copy(sourceExecutable,stage,false);if(File.Exists(pinned))File.Replace(stage,pinned,null);else File.Move(stage,pinned);}finally{if(File.Exists(stage))File.Delete(stage);}
            }
            var record=new ResumeRecord {MachineId=platform.MachineId,UserSid=UserSid,BootBefore=platform.BootId,SnapshotCreated=snapshot.CreatedUtc,ExpectedPhase=snapshot.Phase,CreatedUtc=DateTime.UtcNow.ToString("o")};
            WriteJson(PendingPath,record);
            try{Register(record.UserSid,BuildTaskXml(record.UserSid,pinned));}
            catch(Exception error){try{Remove(record.UserSid);}catch{}File.Delete(PendingPath);throw new InvalidOperationException("重启后自动启动未配置成功，尚未重启："+error.Message,error);}
        }
        public ResumeRecord Pending(IPlatform platform)
        {
            if(!File.Exists(PendingPath))return null;
            if(new FileInfo(PendingPath).Length>16384)throw new InvalidDataException("重启核验记录异常。");
            var value=new JavaScriptSerializer().Deserialize<ResumeRecord>(File.ReadAllText(PendingPath,Encoding.UTF8));
            DateTime created;
            if(value==null||value.Version!=1||value.MachineId!=platform.MachineId||value.UserSid!=UserSid||string.IsNullOrEmpty(value.BootBefore)||string.IsNullOrEmpty(value.SnapshotCreated)||!DateTime.TryParse(value.CreatedUtc,out created)||(value.ExpectedPhase!="Protected"&&value.ExpectedPhase!="Restored"))throw new InvalidDataException("重启核验记录与当前电脑或用户不一致。");
            return value;
        }
        public static bool Ready(ResumeRecord record,string boot)
        {return record!=null&&!string.IsNullOrEmpty(record.BootBefore)&&!string.IsNullOrEmpty(boot)&&record.BootBefore!=boot;}
        public static bool GoalMatches(ResumeRecord record,Snapshot snapshot)
        {return record!=null&&snapshot!=null&&record.SnapshotCreated==snapshot.CreatedUtc&&record.ExpectedPhase==snapshot.Phase;}
        public void Complete(ResumeRecord record,Snapshot snapshot,ScanResult scan,List<string> differences)
        {
            if(record==null||scan==null||!Ready(record,scan.BootId))throw new InvalidOperationException("尚未完成重启，保留自动启动核验任务。");
            if(snapshot!=null&&snapshot.PendingDevices.Count>0)throw new InvalidOperationException("设备恢复尚未完成，保留重启后的继续恢复任务。");
            WriteJson(Path.Combine(root,"last-verification.json"),new {CheckedAt=DateTime.UtcNow.ToString("o"),Expected=record,CurrentPhase=snapshot==null?null:snapshot.Phase,scan.BootId,scan.ComputerName,scan.OsDescription,GoalMatches=GoalMatches(record,snapshot),Differences=differences,Warnings=scan.Warnings,Devices=scan.Devices,Services=scan.Services});
            Remove(record.UserSid);File.Delete(PendingPath);
        }
    }
}
