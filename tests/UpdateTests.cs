using System;

namespace QqmBetterDownload {
    // Offline: only the handling of GitHub's release list is tested, never a
    // real request.
    internal static class UpdateTests {
        internal static void Run(Action<bool, string> check) {
            string page = UpdateCheck.Project + "/releases/tag/v0.2.0";
            var newer = UpdateCheck.Parse("0.1.4", 200, "[{\"tag_name\":\"v0.1.4\"},{\"tag_name\":\"v0.2.0\",\"html_url\":\"" + page + "\"}]");
            check(newer.State == "available" && newer.Version == "0.2.0" && newer.Url == page, "newest release offered with its page");
            check(UpdateCheck.Parse("0.1.9", 200, "[{\"tag_name\":\"v0.1.10\"}]").State == "available", "versions compare numerically");
            check(UpdateCheck.Parse("0.1.4", 200, "[{\"tag_name\":\"v0.1.4\"},{\"tag_name\":\"0.1.3\"}]").State == "latest", "same or older releases are up to date");
            var test = UpdateCheck.Parse("0.1.4", 200, "[{\"tag_name\":\"v0.9.0\",\"draft\":true},{\"tag_name\":\"v0.1.5-beta\",\"prerelease\":true,\"html_url\":\"https://example.com/x\"}]");
            check(test.State == "available" && test.Version == "0.1.5" && test.Message.Contains("测试版") && test.Url == UpdateCheck.Project + "/releases", "test builds count, drafts never, foreign pages never open");
            check(UpdateCheck.Parse("0.1.4", 200, "[]").State == "latest", "no published release counts as up to date");
            check(UpdateCheck.Parse("0.1.4", 403, "").State == "error" && UpdateCheck.Parse("0.1.4", 404, "").State == "error", "rate limits and missing pages reported");
            check(UpdateCheck.Parse("0.1.4", 200, "not json").State == "error" && UpdateCheck.Parse("0.1.4", 200, "{\"tag_name\":\"v1.0\"}").State == "error", "unreadable replies rejected");
        }
    }
}
