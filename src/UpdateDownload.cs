using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteDriverSwitch
{
    internal static class UpdateDownload
    {
        private const string Repository = "drydraft/RemoteDriverSwitch";
        private const string ExecutableName = "远控驱动开关.exe";
        private const int MaxMetadataBytes = 256 * 1024;
        private const int MaxZipBytes = 24 * 1024 * 1024;
        private const int MaxExecutableBytes = 16 * 1024 * 1024;

        private sealed class Asset
        {
            internal Uri Link;
            internal long Size;
            internal string Digest;
        }

        internal static void Stage(Version version, string directory)
        {
            string tag = "v" + version.ToString(3);
            string name = "RemoteDriverSwitch-" + tag + "-win-x64.zip";
            var asset = GetAsset(tag, name);
            string zip = Path.Combine(directory, "release.zip");
            bool created = false;
            try
            {
                using (var file = new FileStream(zip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    created = true;
                    Download(asset, file);
                    file.Position = 0;
                    string digest;
                    using (var sha = SHA256.Create())
                        digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (!string.Equals(asset.Digest, digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包 SHA-256 校验失败。");
                    Extract(file, Path.Combine(directory, "new.exe"), version);
                }
            }
            finally
            {
                if (created) File.Delete(zip);
            }
        }

        private static Asset GetAsset(string tag, string name)
        {
            var uri = new Uri("https://api.github.com/repos/" + Repository + "/releases/tags/" + tag);
            var request = Request(uri, 10000);
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2026-03-10";
            string json;
            using (var timeout = Deadline(request))
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var body = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > MaxMetadataBytes)
                    throw new InvalidDataException("GitHub 发布信息响应异常。");
                using (var stream = response.GetResponseStream())
                    CopyBounded(stream, body, MaxMetadataBytes);
                json = Encoding.UTF8.GetString(body.ToArray());
            }

            var serializer = new JavaScriptSerializer {MaxJsonLength = MaxMetadataBytes, RecursionLimit = 32};
            var release = serializer.Deserialize<Dictionary<string, object>>(json);
            if (release == null || !Equals(Field(release, "tag_name"), tag) ||
                !Equals(Field(release, "draft"), false) || !Equals(Field(release, "prerelease"), false))
                throw new InvalidDataException("GitHub 发布版本信息不匹配。");

            var assets = Field(release, "assets") as IList;
            var matching = (assets ?? (IList)new object[0]).OfType<Dictionary<string, object>>()
                .Where(item => Equals(Field(item, "name"), name)).ToArray();
            if (matching.Length != 1)
                throw new InvalidDataException("GitHub 上找不到唯一的 Windows x64 更新包。");

            var asset = matching[0];
            var expected = new Uri("https://github.com/" + Repository + "/releases/download/" + tag + "/" + name);
            string digest = Field(asset, "digest") as string;
            var size = Field(asset, "size");
            if (!Equals(Field(asset, "browser_download_url"), expected.AbsoluteUri) ||
                !Equals(Field(asset, "state"), "uploaded") ||
                digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest.Substring(7).All(IsHexDigit) ||
                !(size is int || size is long))
                throw new InvalidDataException("GitHub 更新包缺少可信的地址、大小或 SHA-256 校验值。");

            long sizeInBytes = Convert.ToInt64(size, CultureInfo.InvariantCulture);
            if (sizeInBytes <= 0 || sizeInBytes > MaxZipBytes)
                throw new InvalidDataException("GitHub 更新包大小超出限制。");
            return new Asset {Link = expected, Size = sizeInBytes, Digest = digest.Substring(7)};
        }

        private static object Field(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? value : null;
        }

        private static bool IsHexDigit(char value)
        {
            return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');
        }

        private static HttpWebRequest Request(Uri uri, int timeout)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.UserAgent = "RemoteDriverSwitch";
            request.AllowAutoRedirect = false;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            return request;
        }

        private static Timer Deadline(HttpWebRequest request)
        {
            return new Timer(state => request.Abort(), null, request.Timeout, Timeout.Infinite);
        }

        private static void Download(Asset asset, Stream destination)
        {
            Uri link = asset.Link;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var request = Request(link, 30000);
                using (var timeout = Deadline(request))
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        Uri redirected;
                        if (!Uri.TryCreate(link, response.Headers["Location"], out redirected) ||
                            redirected.Scheme != Uri.UriSchemeHttps || !redirected.IsDefaultPort ||
                            redirected.UserInfo.Length != 0 || redirected.Fragment.Length != 0 ||
                            !string.Equals(redirected.Host, "release-assets.githubusercontent.com", StringComparison.Ordinal) ||
                            !redirected.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal))
                            throw new InvalidDataException("GitHub 更新包跳转地址未通过校验。");
                        link = redirected;
                        continue;
                    }

                    if (status != 200 || (response.ContentLength >= 0 && response.ContentLength != asset.Size))
                        throw new InvalidDataException("GitHub 更新包响应大小不匹配。");
                    using (var stream = response.GetResponseStream())
                        if (CopyBounded(stream, destination, asset.Size) != asset.Size)
                            throw new InvalidDataException("GitHub 更新包未下载完整。");
                    return;
                }
            }
            throw new InvalidDataException("GitHub 更新下载重定向次数超限。");
        }

        private static long CopyBounded(Stream source, Stream destination, long maximum)
        {
            var buffer = new byte[8192];
            long total = 0;
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                total += bytesRead;
                if (total > maximum) throw new InvalidDataException("更新文件超过大小限制。");
                destination.Write(buffer, 0, bytesRead);
            }
            return total;
        }

        internal static void Extract(string zipPath, string targetPath, Version version)
        {
            using (var file = File.OpenRead(zipPath)) Extract(file, targetPath, version);
        }

        private static void Extract(Stream zip, string targetPath, Version version)
        {
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, true))
            {
                var executables = archive.Entries
                    .Where(entry => string.Equals(entry.FullName, ExecutableName, StringComparison.OrdinalIgnoreCase))
                    .Take(2).ToArray();
                if (executables.Length != 1 || executables[0].FullName != ExecutableName ||
                    executables[0].Length <= 0 || executables[0].Length > MaxExecutableBytes)
                    throw new InvalidDataException("更新包缺少唯一有效的程序文件。");

                bool created = false;
                try
                {
                    using (var destination = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        using (var source = executables[0].Open())
                            if (CopyBounded(source, destination, executables[0].Length) != executables[0].Length)
                                throw new InvalidDataException("更新程序文件未解压完整。");
                    }
                    CheckExecutable(targetPath, version);
                }
                catch
                {
                    if (created) File.Delete(targetPath);
                    throw;
                }
            }
        }

        private static void CheckExecutable(string path, Version version)
        {
            var expected = new Version(version.Major, version.Minor, version.Build, 0);
            AssemblyName assembly;
            try { assembly = AssemblyName.GetAssemblyName(path); }
            catch (BadImageFormatException error) { throw new InvalidDataException("更新包中的程序文件无效。", error); }
            if (assembly.Version != expected || assembly.ProcessorArchitecture != ProcessorArchitecture.Amd64 ||
                !string.Equals(assembly.Name, Path.GetFileNameWithoutExtension(ExecutableName), StringComparison.Ordinal))
                throw new InvalidDataException("更新程序的程序集名称、版本或架构不匹配。");
            var file = FileVersionInfo.GetVersionInfo(path);
            if (file.FileMajorPart != expected.Major || file.FileMinorPart != expected.Minor ||
                file.FileBuildPart != expected.Build || file.FilePrivatePart != expected.Revision)
                throw new InvalidDataException("更新程序的文件版本不匹配。");
        }
    }
}
