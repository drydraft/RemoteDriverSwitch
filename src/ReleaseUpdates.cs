using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteDriverSwitch
{
    internal static class ReleaseUpdates
    {
        private const string Repository = "drydraft/RemoteDriverSwitch";
        private const int MaxResponseBytes = 256 * 1024;
        private const int TimeoutMilliseconds = 10000;

        internal static Version Check(Version installed)
        {
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.Method = "GET";
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2026-03-10";
            request.UserAgent = "RemoteDriverSwitch";
            request.AllowAutoRedirect = false;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;

            // Also bound the whole response, even if data keeps arriving slowly.
            using (var deadline = new Timer(state => request.Abort(), null, TimeoutMilliseconds, Timeout.Infinite))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > MaxResponseBytes)
                    throw new IOException("GitHub 发布信息响应异常。");

                using (var stream = response.GetResponseStream())
                using (var body = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (body.Length + bytesRead > MaxResponseBytes)
                            throw new IOException("GitHub 发布信息超出大小限制。");
                        body.Write(buffer, 0, bytesRead);
                    }
                    return Parse(Encoding.UTF8.GetString(body.ToArray()), installed);
                }
            }
        }

        internal static Version Parse(string json, Version installed)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxResponseBytes, RecursionLimit = 32 };
            var release = serializer.Deserialize<Dictionary<string, object>>(json);
            object draft, prerelease;
            if (release == null || !release.TryGetValue("draft", out draft) || !(draft is bool) ||
                !release.TryGetValue("prerelease", out prerelease) || !(prerelease is bool))
                throw new InvalidDataException("无法识别 GitHub 发布状态。");

            if ((bool)draft || (bool)prerelease)
                return null;

            object tagValue;
            string tag = release.TryGetValue("tag_name", out tagValue) ? tagValue as string : null;
            Version candidate;
            if (string.IsNullOrEmpty(tag) || tag[0] != 'v' || !Version.TryParse(tag.Substring(1), out candidate) ||
                candidate.Build < 0 || candidate.Revision >= 0 || tag != "v" + candidate.ToString(3))
                throw new InvalidDataException("无法识别 GitHub 发布版本号。");

            var current = new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build));
            return candidate > current ? candidate : null;
        }

        internal static string Page(Version version)
        {
            return "https://github.com/" + Repository + "/releases/tag/v" + version.ToString(3);
        }
    }
}
