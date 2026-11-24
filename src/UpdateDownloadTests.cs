using System;
using System.IO;
using System.IO.Compression;

namespace RemoteDriverSwitch
{
    internal static class UpdateDownloadTests
    {
        internal static void Register(Action<string,Action> test)
        {
            test("update rejects duplicate executable entries",()=>{
                WithZip(new[]{"远控驱动开关.exe","远控驱动开关.exe"},(zip,target)=>{
                    ExpectInvalid(()=>UpdateDownload.Extract(zip,target,new Version(1,2,0)));
                    if(File.Exists(target))throw new Exception("duplicate entries left a staged executable");
                });
            });
            test("update rejects nested executable and does not extract ZIP paths",()=>{
                WithZip(new[]{"sub/远控驱动开关.exe","../sibling.txt"},(zip,target)=>{
                    ExpectInvalid(()=>UpdateDownload.Extract(zip,target,new Version(1,2,0)));
                    if(Directory.GetFiles(Path.GetDirectoryName(target)).Length!=1)
                        throw new Exception("update wrote an unselected ZIP entry");
                });
            });
            test("update deletes an invalid executable without replacing existing file",()=>{
                WithZip(new[]{"远控驱动开关.exe"},(zip,target)=>{
                    ExpectInvalid(()=>UpdateDownload.Extract(zip,target,new Version(1,2,0)));
                    if(File.Exists(target))throw new Exception("invalid executable was left staged");
                    File.WriteAllText(target,"existing");
                    try {UpdateDownload.Extract(zip,target,new Version(1,2,0));}
                    catch(IOException) {
                        if(File.ReadAllText(target)=="existing")return;
                        throw new Exception("preexisting file changed");
                    }
                    throw new Exception("preexisting file was accepted as a destination");
                });
            });
        }

        private static void ExpectInvalid(Action action)
        {
            try{action();}
            catch(InvalidDataException){return;}
            throw new Exception("malformed update was accepted");
        }

        private static void WithZip(string[] entries,Action<string,string> test)
        {
            string root=Path.Combine(Path.GetTempPath(),"RemoteDriverSwitch-zip-test-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string zip=Path.Combine(root,"release.zip"),target=Path.Combine(root,"new.exe");
            try {
                using(var stream=File.Create(zip))
                using(var archive=new ZipArchive(stream,ZipArchiveMode.Create))
                    foreach(string name in entries)
                        using(var body=archive.CreateEntry(name).Open())body.WriteByte(1);
                test(zip,target);
            }finally {
                if(File.Exists(target))File.Delete(target);
                File.Delete(zip);
                Directory.Delete(root);
            }
        }
    }
}
