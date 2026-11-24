using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDriverSwitch
{
    internal sealed class DeviceCommandResult
    {
        public int ExitCode;
        public string Output;
    }
    internal static class DeviceCommandRunner
    {
        private const int MaxOutput=16384;
        public static DeviceCommandResult Run(string executable,string arguments,int timeout,Action<string> diagnostic)
        {
            var output=new StringBuilder();var gate=new object();bool truncated=false;
            Action<string> trace=value=>{if(diagnostic!=null)try{diagnostic(value);}catch{}};
            Action<string,string> append=(channel,line)=>{
                if(line==null)return;
                lock(gate) {
                    string value=channel+line+Environment.NewLine;
                    int room=MaxOutput-output.Length;
                    if(room>0)output.Append(value,0,Math.Min(room,value.Length));
                    if(value.Length>room)truncated=true;
                }
            };
            Encoding encoding;
            try{encoding=Encoding.GetEncoding((int)GetOEMCP());}catch(ArgumentException){encoding=Encoding.Default;}
            var start=new ProcessStartInfo {FileName=executable,Arguments=arguments,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=encoding,StandardErrorEncoding=encoding};
            trace("PnPUtil 调用："+executable+" "+arguments+"；输出编码 "+encoding.CodePage);
            using(var process=new Process {StartInfo=start}) {
                process.OutputDataReceived+=(sender,e)=>append("stdout: ",e.Data);
                process.ErrorDataReceived+=(sender,e)=>append("stderr: ",e.Data);
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
                if(!process.WaitForExit(timeout)) {
                    lock(gate)trace("PnPUtil 等待超时；当前输出："+output.ToString()+(truncated?" [已截断]":""));
                    throw new PendingOperationException("设备操作超过等待时间，请等待该操作结束后重新打开工具恢复。命令及输出已记录日志。");
                }
                // The timed overload does not guarantee asynchronous output events have drained.
                process.WaitForExit();
                string captured;lock(gate)captured=output.ToString();
                trace("PnPUtil 返回码："+process.ExitCode+"；输出"+(truncated?"（已截断）":"")+"："+Environment.NewLine+captured);
                return new DeviceCommandResult {ExitCode=process.ExitCode,Output=captured};
            }
        }
        [DllImport("kernel32.dll")]private static extern uint GetOEMCP();
    }
}
