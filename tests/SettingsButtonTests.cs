using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace QqmBetterDownload {
    // Settings buttons in the preview renderer. The update check is answered
    // offline: "up to date" and failures return to "检查更新" so it can be
    // clicked again, while a found release keeps its download button. The
    // footer ends with the quiet uninstall link.
    internal static class SettingsButtonTests {
        static void Pump(int ms){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);}}
        static void Until(Func<bool> ready){var timer=Stopwatch.StartNew();while(!ready()){if(timer.ElapsedMilliseconds>20000)throw new Exception("Settings buttons timed out");Pump(20);}}
        static string Script(AppWindow page,string script){var work=page.EvaluateHtml(script);Until(()=>work.IsCompleted);return work.GetAwaiter().GetResult();}
        static string Button(AppWindow page){return Script(page,"(function(b){return b.dataset.state+'|'+b.textContent+'|'+b.disabled;})(document.querySelector('[data-update]'))");}
        internal static void Run(Action<bool,string> check){
            using(var page=new AppWindow(true)){
                page.ShowInTaskbar=false;page.StartPosition=FormStartPosition.Manual;page.Location=new Point(-10000,-10000);page.Restore();
                Until(()=>page.HtmlReady||page.HtmlError.Length>0);check(page.HtmlReady,"settings HTML loads for its buttons");Pump(300);
                check(Button(page)=="\"|检查更新|false\"","update button starts ready to check");
                page.UpdateLinger=400;
                foreach(var reply in new[]{new UpdateCheck.Result{State="latest",Version="0.1.4",Message="v0.1.4 已是最新版本。"},new UpdateCheck.Result{State="error",Message="无法连接 GitHub，请检查网络后重试。"}}){
                    var answer=new ManualResetEvent(false);page.UpdateSource=delegate{answer.WaitOne(5000);return reply;};
                    Script(page,"document.querySelector('[data-update]').click()");
                    Until(()=>Button(page)=="\"checking|正在检查…|true\"");answer.Set();
                    Until(()=>Button(page)==(reply.State=="latest"?"\"latest|已是最新|false\"":"\"error|检查失败，重试|false\""));
                    Until(()=>Button(page)=="\"|检查更新|false\"");check(true,"update result "+reply.State+" returns to a clickable check button");
                }
                page.UpdateSource=delegate{return new UpdateCheck.Result{State="available",Version="0.2.0",Url=UpdateCheck.Project+"/releases/latest",Message="发现新版本 v0.2.0。"};};
                Script(page,"document.querySelector('[data-update]').click()");Until(()=>Button(page)=="\"available|下载 v0.2.0|false\"");
                Pump(900);check(Button(page)=="\"available|下载 v0.2.0|false\"","a found release keeps its download button");
                check(Script(page,"(function(l){return l[l.length-1].textContent==='卸载'&&l[l.length-1].hasAttribute('data-uninstall');})(document.querySelectorAll('[data-links] a'))")=="true","uninstall is the last footer link");
                page.Close();
            }
        }
    }
}
