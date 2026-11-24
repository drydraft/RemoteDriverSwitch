using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteDriverSwitch
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
#if TESTS
            if(args.Contains("--inspect")) {
                try {
                    var scan=ScanResult.Capture(new WindowsPlatform());
                    Console.WriteLine(scan.ComputerName+" | "+scan.OsDescription);
                    foreach(var app in scan.Software)Console.WriteLine(app.Name+" | installed="+app.Installed+" | running="+(app.ProcessRunning||app.ServiceRunning)+" | process="+app.ProcessRunning+" | service="+(app.ServiceDetected?app.ServiceRunning.ToString():"not detected")+" | "+app.Version+" | "+app.InstallPath);
                    foreach(var target in Catalog.Targets)foreach(var d in scan.Devices.Where(target.Matches))Console.WriteLine(target.Id+" | "+d.InstanceId+" | "+d.Inf+" | disabled="+d.Disabled);
                    foreach(var warning in scan.Warnings)Console.WriteLine("WARNING "+warning);
                    return scan.Warnings.Count==0?0:2;
                }catch(Exception ex){Console.WriteLine(ex);return 1;}
            }
            return Tests.Run(args.Length>0?args[0]:null);
#else
            if(args.Length>0&&args[0]=="--apply-update")return PortableUpdate.Apply(args);
            IDisposable updateReceipt=null;
            if(args.Length>0&&args[0]=="--finish-update") {
                try {if(!PortableUpdate.Resume(args,out updateReceipt))return 1;}
                catch(Exception){return 1;}
            }
            using(updateReceipt)return RunUi(args,updateReceipt);
#endif
        }
#if !TESTS
        private static bool UpdateInProgress()
        {
            Mutex updateGate;
            if(!Mutex.TryOpenExisting(PortableUpdate.GateName,out updateGate))return false;
            updateGate.Dispose();return true;
        }
        private static int RunUi(string[] args,IDisposable updateReceipt)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            bool preview=false;
#if PREVIEW
            preview=true;
#endif
            preview|=args.Contains("--preview")||args.Contains("--render-preview")||args.Contains("--ui-smoke");
            if(!preview&&updateReceipt==null&&UpdateInProgress())return 0;
            string instance=preview?"Local\\RemoteDriverSwitch.Preview.v2":"Global\\RemoteDriverSwitch.Ui.v2";bool created;
            using(var mutex=new Mutex(true,instance,out created))using(var activation=new EventWaitHandle(false,EventResetMode.AutoReset,instance+".Activate")) {
                if(!created){activation.Set();return 0;}
                if(!preview&&updateReceipt==null&&UpdateInProgress())return 0;
                try {
                    IPlatform platform;IStore store;string dataPath;
                    if(preview){platform=new FakePlatform();store=new MemoryStore();dataPath="模拟预览";}
                    else {
                        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("请右键程序，选择“以管理员身份运行”。");
                        platform=new WindowsPlatform();dataPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteDriverSwitch");store=new FileStore(dataPath,true);
                    }
                    string portablePath=updateReceipt==null?Application.ExecutablePath:PortableUpdate.TargetPath(updateReceipt);
                    using(var form=new MainForm(platform,store,dataPath,preview,portablePath)) {
                        var handle=form.Handle;
                        if(updateReceipt!=null)form.Shown+=(sender,e)=>PortableUpdate.Ready(updateReceipt);
                        var wait=ThreadPool.RegisterWaitForSingleObject(activation,(state,timedOut)=>{try{form.BeginInvoke((Action)form.ShowMain);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
                        try {
                            if(args.Contains("--render-preview")||args.Contains("--ui-smoke")) {
                                form.RefreshData();form.Show();Application.DoEvents();
                                if(args.Contains("--ui-smoke")) {
                                    form.Close();Application.DoEvents();if(form.IsDisposed||form.Visible||!form.TrayVisible)throw new Exception("关闭后托盘驻留检查失败。");
                                    form.RestoreWindow();Application.DoEvents();if(!form.Visible)throw new Exception("托盘恢复窗口检查失败。");
                                    var update=form.Controls.OfType<Button>().Single(button=>button.Text=="检查更新");
                                    form.RecordUpdate(new Version(9,0,0));
                                    if(update.Text!="立即更新"||!update.Enabled)throw new Exception("立即更新入口未显示。");
                                    form.RecordUpdate(null);
                                    if(update.Text!="检查更新")throw new Exception("更新检查入口未恢复。");
                                    File.WriteAllText(args[args.Length-1],"PASS compact window "+form.ClientSize+Environment.NewLine+"PASS close hides window and keeps tray"+Environment.NewLine+"PASS show restores window"+Environment.NewLine+"PASS update and download controls");
                                } else using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(args[args.Length-1],System.Drawing.Imaging.ImageFormat.Png);}
                                form.ExitApplication();return 0;
                            }
                            Application.Run(form);
                        }finally{wait.Unregister(null);}
                    }
                    return 0;
                }catch(Exception ex){MessageBox.Show(ex.Message,"远控驱动开关",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
            }
        }
#endif
    }
    public sealed class MainForm : Form
    {
        private readonly IPlatform platform;private readonly IStore store;private readonly SwitchEngine engine;private readonly WindowsResume resume;
        private readonly string dataPath,portablePath;private readonly bool preview;
        private Label computer,mode,summary,backupInfo;private readonly Dictionary<string,Label> softwareLabels=new Dictionary<string,Label>();
        private Button protect,restore,refresh,logs,updateButton;private ListView list;private TextBox activity;
        private readonly ToolTip tips=new ToolTip();private NotifyIcon tray;private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayRefresh,trayProtect,trayRestore,trayExit,trayUpdate,trayDownload;
        private readonly System.Windows.Forms.Timer updateTimer;
        private static readonly Version AppVersion=new Version(typeof(Program).Assembly.GetName().Version.ToString(3));
        private Version availableUpdate;
        private readonly HashSet<Version> notifiedUpdates=new HashSet<Version>();
        private bool checkingUpdate,installingUpdate;
        private bool busy,exitRequested,initialScan=true,allowProtect,allowRestore;
        public bool TrayVisible {get{return tray!=null&&tray.Visible;}}
        public MainForm(IPlatform platform,IStore store,string dataPath,bool preview,string portablePath=null)
        {
            this.platform=platform;this.store=store;this.dataPath=dataPath;this.preview=preview;this.portablePath=portablePath??Application.ExecutablePath;engine=new SwitchEngine(platform,store);if(!preview)resume=new WindowsResume(dataPath);
            Text="远控驱动开关 "+AppVersion+(preview?"（预览）":"");Font=SystemFonts.MessageBoxFont;
            AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(740,576);FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
            BackColor=SystemColors.Control;ForeColor=SystemColors.ControlText;
            try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
            BuildUi();BuildTray();SetBusy(true);
            engine.Progress=message=>{try{if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)(()=>Append(message)));}catch(InvalidOperationException){}};
            var windows=platform as WindowsPlatform;
            if(windows!=null)windows.Diagnostic=message=>{store.Log(message);try{if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)(()=>Append(message)));}catch(InvalidOperationException){}};
            Shown+=async(sender,e)=>{if(initialScan){initialScan=false;await RefreshAsync(true);}};
            if(!preview) {
                updateTimer=new System.Windows.Forms.Timer {Interval=6*60*60*1000};
                updateTimer.Tick+=async(sender,e)=>await CheckUpdates(false);
                Shown+=async(sender,e)=>{updateTimer.Start();await CheckUpdates(false);};
            }
            Resize+=(sender,e)=>{if(WindowState==FormWindowState.Minimized)Hide();};
            FormClosing+=(sender,e)=>{if(e.CloseReason==CloseReason.UserClosing&&!exitRequested){e.Cancel=true;Hide();}};
        }
        private Label AddLabel(Control parent,string text,int x,int y,int width,int height,bool bold=false)
        {
            var label=new Label {Text=text,Location=new Point(x,y),Size=new Size(width,height),AutoEllipsis=true};if(bold)label.Font=new Font(Font,FontStyle.Bold);parent.Controls.Add(label);return label;
        }
        private Button ButtonAt(string text,int x,int y,int width)
        {var button=new Button {Text=text,Location=new Point(x,y),Size=new Size(width,30),FlatStyle=FlatStyle.System};Controls.Add(button);return button;}
        private void BuildUi()
        {
            var group=new GroupBox {Text="电脑与远程软件",Location=new Point(12,8),Size=new Size(716,126)};Controls.Add(group);
            computer=AddLabel(group,"正在扫描本机系统、安装记录、服务和设备…",12,21,692,31);
            int y=57;foreach(string id in new[]{"uu","td","oray"}) {softwareLabels[id]=AddLabel(group,"扫描中…",12,y,692,20);y+=20;}
            mode=AddLabel(this,"正在扫描，请稍候…",14,145,714,22,true);summary=AddLabel(this,"扫描完成后可切换。",14,169,714,22);
            list=new ListView {Location=new Point(12,196),Size=new Size(716,189),View=View.Details,FullRowSelect=true,GridLines=true,HideSelection=false,BorderStyle=BorderStyle.Fixed3D,HeaderStyle=ColumnHeaderStyle.Nonclickable,ShowItemToolTips=true};
            list.Columns.Add("远控组件",211);list.Columns.Add("用途",118);list.Columns.Add("设备状态",84);list.Columns.Add("专用驱动",104);list.Columns.Add("本次核验",173);Controls.Add(list);
            backupInfo=AddLabel(this,"首次切换前保存本机原始配置。",14,391,714,20);
            protect=ButtonAt("禁用并重启（游戏）",12,418,217);protect.Click+=async(s,e)=>await Switch(true);
            restore=ButtonAt("恢复并重启（远控）",239,418,217);restore.Click+=async(s,e)=>await Switch(false);
            refresh=ButtonAt("重新扫描",466,418,116);refresh.Click+=async(s,e)=>await RefreshAsync();
            logs=ButtonAt("备份与日志",592,418,136);logs.Click+=(s,e)=>{if(!preview)Process.Start(new ProcessStartInfo {FileName="explorer.exe",Arguments=WindowsPlatform.Quote(dataPath),UseShellExecute=true});};
            activity=new TextBox {Location=new Point(12,458),Size=new Size(716,76),Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=SystemColors.Window,BorderStyle=BorderStyle.Fixed3D};Controls.Add(activity);
            AddLabel(this,"关闭窗口后驻留托盘；右键托盘图标可退出。",14,546,476,20);
            updateButton=ButtonAt("检查更新",506,541,112);
            updateButton.Click+=async(s,e)=>{if(availableUpdate!=null)await InstallUpdate();else await CheckUpdates(true);};
            var exit=ButtonAt("退出",628,541,100);exit.Click+=(s,e)=>ExitApplication();
        }
        private void BuildTray()
        {
            trayMenu=new ContextMenuStrip();trayMenu.Items.Add("打开窗口",null,(s,e)=>ShowMain());
            trayRefresh=new ToolStripMenuItem("重新扫描",null,async(s,e)=>{RestoreWindow();await RefreshAsync();});trayMenu.Items.Add(trayRefresh);
            trayProtect=new ToolStripMenuItem("禁用并重启（游戏）",null,async(s,e)=>{RestoreWindow();await Switch(true);});trayMenu.Items.Add(trayProtect);
            trayRestore=new ToolStripMenuItem("恢复并重启（远控）",null,async(s,e)=>{RestoreWindow();await Switch(false);});trayMenu.Items.Add(trayRestore);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayUpdate=new ToolStripMenuItem("检查更新",null,async(s,e)=>{RestoreWindow();await CheckUpdates(true);});trayMenu.Items.Add(trayUpdate);
            trayDownload=new ToolStripMenuItem("立即更新",null,async(s,e)=>await InstallUpdate()) {Visible=false};trayMenu.Items.Add(trayDownload);
            trayMenu.Items.Add(new ToolStripSeparator());trayExit=new ToolStripMenuItem("退出",null,(s,e)=>ExitApplication());trayMenu.Items.Add(trayExit);
            tray=new NotifyIcon {Text="远控驱动开关",Icon=Icon??SystemIcons.Application,Visible=true,ContextMenuStrip=trayMenu};tray.DoubleClick+=(s,e)=>ShowMain();
            tray.BalloonTipClicked+=(s,e)=>RestoreWindow();
        }
        public async void ShowMain(){RestoreWindow();if(!busy)await RefreshAsync();}
        public void RestoreWindow(){Show();WindowState=FormWindowState.Normal;Activate();BringToFront();}
        public void ExitApplication(){if(busy){RestoreWindow();MessageBox.Show(this,"扫描或切换尚未结束，请完成后退出。","正在处理");return;}exitRequested=true;Close();}
        protected override void Dispose(bool disposing)
        {if(disposing){if(updateTimer!=null)updateTimer.Dispose();if(tray!=null){tray.Visible=false;tray.Dispose();}if(trayMenu!=null)trayMenu.Dispose();tips.Dispose();}base.Dispose(disposing);}
        private void RenderUpdateControls()
        {
            updateButton.Text=installingUpdate?"正在更新…":availableUpdate!=null?"立即更新":checkingUpdate?"检查中…":"检查更新";
            updateButton.Enabled=!busy&&(availableUpdate!=null||!checkingUpdate);
            tips.SetToolTip(updateButton,availableUpdate==null?"检查 GitHub 上的正式版本":"下载并安装 v"+availableUpdate);
            trayUpdate.Enabled=!busy&&!checkingUpdate;
            trayDownload.Visible=availableUpdate!=null;
            trayDownload.Enabled=!busy;
            if(availableUpdate!=null)trayDownload.Text="立即更新到 v"+availableUpdate;
        }
        internal void RecordUpdate(Version found)
        {
            availableUpdate=found;
            RenderUpdateControls();
            if(found==null||!notifiedUpdates.Add(found))return;
            Append("发现新版 v"+found+"。");
            if(!preview)tray.ShowBalloonTip(6000,"远控驱动开关有新版","v"+found+" 已发布，点击打开更新入口。",ToolTipIcon.Info);
        }
        private async Task CheckUpdates(bool manual)
        {
            if(checkingUpdate||installingUpdate)return;
            checkingUpdate=true;RenderUpdateControls();
            try {
                var found=preview?null:await Task.Run(()=>ReleaseUpdates.Check(AppVersion));
                if(IsDisposed||Disposing)return;
                RecordUpdate(found);
                if(manual&&found==null)MessageBox.Show(this,"当前已是最新版本 v"+AppVersion+"。","检查更新");
            }catch(Exception) {
                if(manual&&!IsDisposed&&!Disposing)MessageBox.Show(this,"暂时无法检查更新，请确认能访问 GitHub 后重试。","检查更新",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }finally {
                checkingUpdate=false;
                if(!IsDisposed&&!Disposing)RenderUpdateControls();
            }
        }
        private async Task InstallUpdate()
        {
            if(preview||busy||availableUpdate==null)return;
            RestoreWindow();
            var version=availableUpdate;
            string executable=portablePath;
            if(!PortableUpdate.IsAvailable(dataPath,executable)) {
                if(MessageBox.Show(this,"当前无法就地更新。请等重启核验结束，从托盘退出工具，再从可写文件夹打开原便携版。\n\n现在打开 GitHub 下载页？","立即更新",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes)OpenReleasePage(version);
                return;
            }
            installingUpdate=true;
            SetBusy(true);
            Append("正在下载并校验 v"+version+"。");
            try {
                await Task.Run(()=>PortableUpdate.Start(version,dataPath,executable));
                exitRequested=true;Close();
            }catch(Exception error) {
                installingUpdate=false;SetBusy(false);
                Append("更新未完成："+error.Message);
                if(MessageBox.Show(this,error.Message+"\n\n打开 GitHub 下载页？","更新未完成",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes)OpenReleasePage(version);
            }
        }
        private void OpenReleasePage(Version version)
        {
            try {using(var browser=Process.Start(new ProcessStartInfo {FileName=ReleaseUpdates.Page(version),UseShellExecute=true})){}}
            catch(System.ComponentModel.Win32Exception) {
                MessageBox.Show("无法打开浏览器。可以在 GitHub 搜索 drydraft/RemoteDriverSwitch 查看新版。","打开下载页",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
        }
        private void Append(string text)
        {if(activity.TextLength>32000)activity.Text=activity.Text.Substring(activity.TextLength-16000);activity.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+text+Environment.NewLine);}
        private void SetBusy(bool value)
        {busy=value;protect.Enabled=trayProtect.Enabled=!value&&allowProtect;restore.Enabled=trayRestore.Enabled=!value&&allowRestore;refresh.Enabled=logs.Enabled=trayRefresh.Enabled=trayExit.Enabled=!value;UseWaitCursor=value;RenderUpdateControls();}
        public void RefreshData()
        {
            initialScan=false;SetBusy(true);try{var scan=ScanResult.Capture(platform);var snapshot=engine.ReconcileRestoredDevices(engine.Current(),scan);ApplyScan(scan,snapshot,snapshot==null?new List<string>():engine.ConfigurationDrift(snapshot,scan));}finally{SetBusy(false);}
        }
        private async Task RefreshAsync(bool startup=false)
        {
            if(busy&&!startup)return;SetBusy(true);mode.Text="正在扫描本机…";
            try {
                Snapshot snapshot=null;List<string> drift=null;string continuationError=null,startupRepairError=null;bool startupRepaired=false;
                var scan=await Task.Run(()=>{
                    var value=ScanResult.Capture(platform);snapshot=engine.ReconcileRestoredDevices(engine.Current(),value);
                    if(resume!=null&&snapshot!=null&&snapshot.Phase=="Protected"&&snapshot.AppliedBoot!=value.BootId) {
                        var pending=resume.Pending(platform);
                        if(WindowsResume.Ready(pending,value.BootId)&&WindowsResume.GoalMatches(pending,snapshot)&&pending.BootBefore==snapshot.AppliedBoot) {
                            try {startupRepaired=engine.RepairStartupAfterReboot(snapshot,value);}
                            catch(Exception error){startupRepairError="远控自启补处理未完成："+error.Message;try{store.Log(startupRepairError);}catch(IOException){}catch(UnauthorizedAccessException){}}
                            if(startupRepaired||startupRepairError!=null){snapshot=engine.Current();value=ScanResult.Capture(platform);}
                        }
                    }
                    if(resume!=null&&snapshot!=null&&snapshot.Phase=="Restored"&&snapshot.PendingDevices.Count>0&&snapshot.AppliedBoot!=value.BootId) {
                        var pending=resume.Pending(platform);
                        if(WindowsResume.Ready(pending,value.BootId)&&WindowsResume.GoalMatches(pending,snapshot)) {
                            try {
                                engine.ContinueRestoreAfterReboot();snapshot=engine.Current();
                                if(snapshot.PendingDevices.Count>0)resume.Prepare(platform,snapshot,Application.ExecutablePath);
                            }catch(Exception error){continuationError=error.Message;store.Log("重启后继续恢复未完成："+error.Message);snapshot=engine.Current();}
                            value=ScanResult.Capture(platform);
                        }
                    }
                    drift=snapshot==null?new List<string>():engine.ConfigurationDrift(snapshot,value);
                    if(startupRepairError!=null)drift.Insert(0,startupRepairError);
                    return value;
                });ApplyScan(scan,snapshot,drift);if(startupRepaired)Append("已补处理重新出现的远控登录启动项，并重新扫描。");if(continuationError!=null)Append("恢复仍需处理："+continuationError);
            }catch(Exception error){allowProtect=allowRestore=false;mode.Text="扫描未完成";summary.Text=error.Message;tips.SetToolTip(summary,error.ToString());Append(error.Message);}finally{SetBusy(false);}
        }
        private static string StartText(int start){return start==4?"禁用":start==0?"引导":start==1?"系统":start==2?"自动":"手动";}
        private void ApplyScan(ScanResult scan,Snapshot snapshot,List<string> drift)
        {
            bool originalRuntimeRestored=snapshot!=null&&snapshot.Phase=="Restored"&&snapshot.PendingDevices.Count==0&&snapshot.AppliedBoot==scan.BootId&&engine.CanProtect(snapshot,scan);
            computer.Text=scan.ComputerName+"  |  "+scan.OsDescription;
            foreach(var app in scan.Software) {
                Label label;if(!softwareLabels.TryGetValue(app.Id,out label))continue;
                string status=app.Installed
                    ?"程序进程"+(app.ProcessRunning?"运行中":"未发现")+"  |  远控服务"+(app.ServiceDetected?(app.ServiceRunning?"运行中":"已停止"):"未发现")
                    :"未检测到";
                label.Text=app.Name+"："+status+(app.Version.Length>0?"  v"+app.Version:"")+(app.InstallPath.Length>0?"  |  "+app.InstallPath:"");
                tips.SetToolTip(label,label.Text+Environment.NewLine+string.Join(Environment.NewLine,app.Evidence));
            }
            list.BeginUpdate();list.Items.Clear();int found=0;
            foreach(var target in Catalog.Targets) {
                var matches=scan.Devices.Where(target.Matches).ToList();var saved=snapshot==null?new List<DeviceRecord>():snapshot.Devices.Where(r=>r.TargetId==target.Id).ToList();
                if(matches.Count==0&&saved.Count==0)continue;found+=matches.Count;
                var services=target.DriverServices.Where(scan.Services.ContainsKey).Select(n=>scan.Services[n]).ToList();
                string driver=services.Count==0?"设备级切换":string.Join(" / ",services.Select(s=>StartText(s.Start)+(s.Running?"·已加载":"·未加载")));
                string state=matches.Count==0?"未枚举":matches.All(d=>d.Disabled)?"已禁用":matches.Any(d=>d.Disabled)?"部分禁用":"已启用";
                if(snapshot!=null&&snapshot.Phase=="Protected"&&saved.Any(r=>r.DriverOnly))state=snapshot.AppliedBoot==scan.BootId?"重启后停用":matches.Any(d=>d.Started)?"仍在运行":"驱动已停用";
                bool relevant=saved.Count>0||(snapshot!=null&&services.Any(s=>snapshot.Drivers.Any(d=>d.Name==s.Name)));bool bad=drift.Contains(target.Name)||services.Any(s=>drift.Contains(s.Name));
                string check=snapshot==null||!relevant?"—":bad?"与目标不一致":originalRuntimeRestored?"已恢复":snapshot.AppliedBoot==scan.BootId?"配置已写入，待重启":"符合目标";
                if(snapshot!=null&&saved.Any(r=>snapshot.PendingDevices.Contains(r.InstanceId,StringComparer.OrdinalIgnoreCase)))check="重启后继续恢复";
                var row=new ListViewItem(new[]{target.Name,target.Function,state,driver,check});row.ToolTipText=string.Join(Environment.NewLine,matches.Select(d=>d.InstanceId+" | "+d.Inf+" | "+d.Provider+" | "+(d.Started?"设备已启动":"设备未启动")+" | 问题码 "+d.Problem));if(bad)row.ForeColor=Color.DarkRed;list.Items.Add(row);
            }
            foreach(var d in scan.Devices.Where(d=>Catalog.DeviceProduct(d)!=null&&!Catalog.Targets.Any(t=>t.Matches(d)))) {
                var row=new ListViewItem(new[]{d.Name??d.InstanceId,"厂商设备待适配",d.Disabled?"已禁用":"已启用","只读展示","未纳入切换"});row.ToolTipText=d.InstanceId+" | "+d.Provider+" | "+d.Inf;list.Items.Add(row);
            }
            list.EndUpdate();bool sameBoot=snapshot!=null&&snapshot.AppliedBoot==scan.BootId;bool unfinished=snapshot!=null&&snapshot.Phase!="Protected"&&snapshot.Phase!="Restored";
            allowProtect=engine.CanProtect(snapshot,scan)&&(found>0||scan.Startups.Count>0||scan.Services.Values.Any(s=>Catalog.AppExeMatches(s.Name,Path.GetFileName(SoftwareInventory.ExecutablePath(s.ImagePath)))));allowRestore=snapshot!=null;
            if(snapshot==null){mode.Text="扫描完成：已识别 "+found+" 个可切换组件";summary.Text="禁用或恢复后重启；登录桌面后自动打开并核验本次结果。";}
            else if(unfinished){mode.Text="上次切换尚未完成";summary.Text="原始备份已保留，请点击“恢复并重启”继续恢复。";}
            else if(snapshot.PendingDevices.Count>0){mode.Text="恢复配置已保存 · 等待重启";summary.Text="重启登录后继续恢复 "+snapshot.PendingDevices.Count+" 个设备，再核验实际状态。";}
            else if(originalRuntimeRestored){mode.Text="远控模式：原状态已恢复";summary.Text="设备、驱动、远控服务和已备份的登录启动项已恢复。";}
            else {mode.Text=(snapshot.Phase=="Protected"?"游戏模式":"远控模式")+"："+(sameBoot?"等待重启":drift.Count==0?"核验通过":"发现 "+drift.Count+" 项不一致");summary.Text=drift.Count==0?(sameBoot?"当前仅确认配置写入，重启后检查实际加载状态。":"已核对设备、远控服务和已识别的登录启动项。"):"待核对："+string.Join("、",drift.Take(4))+(drift.Count>4?" 等":"");}
            if(scan.Warnings.Count>0){summary.Text="扫描提示："+scan.Warnings[0];Append(string.Join("；",scan.Warnings));if(!sameBoot&&snapshot!=null)mode.Text="扫描含警告，核验尚未通过";}
            tips.SetToolTip(summary,summary.Text+(drift.Count>0?Environment.NewLine+string.Join(Environment.NewLine,drift):""));
            backupInfo.Text=snapshot==null?"已识别 "+scan.Startups.Count+" 个远控登录启动项；切换前备份原配置。":"本机备份："+snapshot.Devices.Count+" 个设备、"+snapshot.Drivers.Count+" 个专用驱动、"+snapshot.Apps.Count+" 个远控服务、"+snapshot.Startups.Count+" 个登录启动项。";
            if(preview)backupInfo.Text="预览使用模拟数据，切换和重启均为模拟。";Append("扫描完成："+scan.Software.Count(s=>s.Installed)+" 款远控软件，"+found+" 个已适配组件。");
            if(resume!=null)try {
                var pending=resume.Pending(platform);
                if(WindowsResume.Ready(pending,scan.BootId)) {
                    var result=new List<string>(drift);if(!WindowsResume.GoalMatches(pending,snapshot)){result.Add("备份或目标模式已变化");mode.Text="重启核验目标已变化，请核对";}
                    if(scan.Warnings.Count>0)result.Add("扫描含警告，请重新核对");
                    if(snapshot!=null&&snapshot.PendingDevices.Count>0)Append("待恢复任务仍保留，完成重启后继续处理。");
                    else {resume.Complete(pending,snapshot,scan,result);Append(result.Count==0?"重启后核验通过；本次自动启动任务已移除。":"重启后核验发现不一致："+string.Join("、",result)+"。报告已保存。");}
                }else if(pending!=null)Append("已安排本次重启核验，等待重启并登录当前账户。");
            }catch(Exception error){Append("自动启动核验记录处理失败："+error.Message);}
        }
        private async Task Switch(bool game)
        {
            if(busy||(game?!allowProtect:!allowRestore))return;
            string text=game?"将保存本机原始配置，停用已识别的远控登录启动项和服务，关闭后台进程，然后禁用对应驱动组件。\n远控连接和这三款软件正在执行的任务会中断。重启后如发现已识别的启动项回写，工具会自动补处理。":"将按本机备份恢复设备、远控服务和登录启动项的原始配置。";
            if(MessageBox.Show(this,text+"\n\n完成后进入重启倒计时。请先保存文件。登录桌面后，工具将自动打开并核验。",game?"禁用并重启":"恢复并重启",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning)!=DialogResult.OK)return;
            SetBusy(true);mode.Text="正在切换，请稍候…";
            Exception failure=null;
            try {
                await Task.Run(()=>{if(game)engine.Protect();else engine.Restore();if(resume!=null){resume.Prepare(platform,engine.Current(),Application.ExecutablePath);store.Log("已安排本次重启后的自动启动核验。");}});
            }catch(Exception error){failure=error;}
            SetBusy(false);await RefreshAsync();
            if(failure!=null){MessageBox.Show(this,failure.Message+"\n\n原始备份已保留，可查看日志并重试恢复。","切换未完成",MessageBoxButtons.OK,MessageBoxIcon.Error);return;}
            using(var dialog=new RestartDialog(()=>engine.Restart(),preview))dialog.ShowDialog(this);
        }
    }
    public sealed class RestartDialog : Form
    {
        private readonly Action reboot;private readonly bool preview;private readonly Label countdown;private readonly System.Windows.Forms.Timer timer;private int remaining=20;
        public RestartDialog(Action reboot,bool preview)
        {
            this.reboot=reboot;this.preview=preview;Text="配置已保存";Font=SystemFonts.MessageBoxFont;ClientSize=new Size(440,172);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
            countdown=new Label {Location=new Point(16,18),Size=new Size(408,25),Font=new Font(Font,FontStyle.Bold)};Controls.Add(countdown);
            Controls.Add(new Label {Text="重启并登录当前账户后，工具会自动打开并核验。\n请确认文件已保存。",Location=new Point(16,51),Size=new Size(408,40)});
            var now=new Button {Text="立即重启",Location=new Point(16,111),Size=new Size(185,31)};now.Click+=(s,e)=>Restart();Controls.Add(now);
            var later=new Button {Text="稍后重启",Location=new Point(215,111),Size=new Size(209,31),DialogResult=DialogResult.Cancel};Controls.Add(later);CancelButton=later;
            timer=new System.Windows.Forms.Timer {Interval=1000};timer.Tick+=(s,e)=>{remaining--;UpdateText();if(remaining<=0)Restart();};UpdateText();Shown+=(s,e)=>timer.Start();FormClosed+=(s,e)=>timer.Dispose();
        }
        private void UpdateText(){countdown.Text=(preview?"模拟预览：":"")+remaining+" 秒后重启电脑";}
        private void Restart(){timer.Stop();try{if(preview)MessageBox.Show(this,"模拟重启完成。","界面预览");else reboot();DialogResult=DialogResult.OK;Close();}catch(Exception error){countdown.Text="尚未重启";MessageBox.Show(this,error.Message,"重启未完成",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    }
}
