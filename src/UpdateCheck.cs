using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace QqmBetterDownload {
    // Runs only when the user clicks “检查更新” in settings: one request for
    // the recent GitHub releases, nothing uploaded, nothing downloaded or run.
    // A newer release opens in the browser, where the user gets the installer.
    internal static class UpdateCheck {
        internal const string Project = "https://github.com/xiaoming6680/QQM-BetterDownload";
        // The release list, not /releases/latest: test builds are published as
        // pre-releases, which /latest never returns.
        const string Latest = "https://api.github.com/repos/xiaoming6680/QQM-BetterDownload/releases?per_page=20";
        internal sealed class Result {
            internal string State = "", Version = "", Url = "", Message = "";
        }
        internal static Result Run(string current) {
            try {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(Latest);
                request.UserAgent = "BetterDownload/" + current; request.Accept = "application/vnd.github+json";
                request.Timeout = request.ReadWriteTimeout = 10000;
                if (request.Proxy != null) request.Proxy.Credentials = CredentialCache.DefaultCredentials;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return Parse(current, (int)response.StatusCode, reader.ReadToEnd());
            } catch (WebException e) {
                var response = e.Response as HttpWebResponse;
                if (response != null) using (response) return Parse(current, (int)response.StatusCode, "");
                return Failed("无法连接 GitHub，请检查网络后重试。");
            } catch (Exception e) { return Failed("检查更新失败：" + e.Message); }
        }
        internal static Result Parse(string current, int status, string json) {
            if (status == 403 || status == 429) return Failed("GitHub 暂时限制了查询，请稍后重试。");
            if (status != 200) return Failed("GitHub 返回错误 " + status + "，请稍后重试。");
            object[] releases = null;
            try { releases = new JavaScriptSerializer().DeserializeObject(json) as object[]; } catch (ArgumentException) { } catch (InvalidOperationException) { }
            string mine = Numbers(current);
            if (releases == null || mine == null) return Failed("无法识别 GitHub 返回的版本信息。");
            // The highest version wins; pre-releases (test builds) count, drafts never do.
            Dictionary<string, object> best = null; Version newest = null;
            foreach (object item in releases) {
                var release = item as Dictionary<string, object>; object tag, draft;
                if (release == null || release.TryGetValue("draft", out draft) && draft is bool && (bool)draft) continue;
                string offered = release.TryGetValue("tag_name", out tag) ? Numbers(tag as string) : null;
                if (offered != null && (newest == null || new Version(offered) > newest)) { newest = new Version(offered); best = release; }
            }
            if (best == null) return new Result { State = "latest", Message = "GitHub 上还没有发布版本。" };
            if (newest <= new Version(mine)) return new Result { State = "latest", Version = newest.ToString(), Message = "v" + mine + " 已是最新版本。" };
            // Only this repository's release pages are ever opened.
            object page, pre; string url = best.TryGetValue("html_url", out page) ? page as string : null;
            if (url == null || !url.StartsWith(Project + "/releases/", StringComparison.Ordinal) || url.IndexOfAny(new[] { '"', ' ', '\\' }) >= 0) url = Project + "/releases";
            bool test = best.TryGetValue("prerelease", out pre) && pre is bool && (bool)pre;
            return new Result { State = "available", Version = newest.ToString(), Url = url, Message = "发现" + (test ? "测试版" : "新版本") + " v" + newest + "，下载新的安装器运行即可更新。" };
        }
        static Result Failed(string message) { return new Result { State = "error", Message = message }; }
        // "v0.1.5" or "0.1.5-beta" → "0.1.5"; anything else is not a version.
        static string Numbers(string text) {
            text = (text ?? "").Trim(); if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            int suffix = text.IndexOfAny(new[] { '-', '+' }); if (suffix >= 0) text = text.Substring(0, suffix);
            Version parsed; return Version.TryParse(text, out parsed) ? parsed.ToString() : null;
        }
    }
}
