using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace QqmBetterDownload {
    internal static class PreviewPageTests {
        static void Pump(int ms){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);}}
        static void Until(Func<bool> ready){var timer=Stopwatch.StartNew();while(!ready()){if(timer.ElapsedMilliseconds>20000)throw new Exception("Settings preview timed out");Pump(20);}}
        static string Script(AppWindow page,string script){var work=page.EvaluateHtml(script);Until(()=>work.IsCompleted);return work.GetAwaiter().GetResult();}
        internal static void Run(Action<bool,string> check){
            using(var page=new AppWindow(true)){
                page.ShowInTaskbar=false;page.StartPosition=FormStartPosition.Manual;page.Location=new Point(-10000,-10000);page.Restore();
                Until(()=>page.HtmlReady||page.HtmlError.Length>0);check(page.HtmlReady,"settings HTML loads in preview renderer");Pump(300);
                check(Script(page,"document.body.dataset.ready==='true' && document.querySelectorAll('input').length===0")=="true","settings need no manual paths");
                // A fresh profile opens on the first-run notice; agreeing reveals the settings.
                check(page.IntroPending && Script(page,"document.body.getAttribute('data-view')==='intro' && document.querySelector('.nbd-panel').offsetParent===null && document.querySelector('[data-intro-agree]').textContent==='同意并继续'")=="true","first visit shows the notice instead of settings");
                Script(page,"document.querySelector('[data-intro-agree]').click()");Pump(400);
                check(!page.IntroPending,"agreeing is recorded by the host");
                check(Script(page,"document.body.getAttribute('data-view')==='settings' && document.querySelector('.nbd-intro').offsetParent===null && document.querySelector('.nbd-panel').offsetParent!==null && parseFloat(document.querySelector('[data-notify] .nbd-thumb').style.width)>20")=="true","agreeing reveals settings with measured segments");
                Script(page,"document.querySelector('[data-toggle]').click()");Pump(100);
                check(Script(page,"document.querySelector('[data-toggle]').getAttribute('aria-checked')==='false' && document.querySelector('[data-scan]').disabled")=="true","toggle and disabled scan must round-trip");
                Script(page,"document.querySelector('[data-toggle]').click();document.querySelector('[data-card-style] [data-value=compact]').click()");Pump(100);
                check(Script(page,"document.querySelector('[data-card-style] [data-value=compact]').getAttribute('aria-checked')==='true'")=="true","card style must round-trip");
                Script(page,"document.querySelector('[data-scan]').click()");Pump(100);
                check(Script(page,"document.querySelector('[data-scan-note]').textContent.indexOf('预览模式')>=0")=="true","scan action must show its result");
                Script(page,"document.querySelector('[data-preview]').click()");Pump(150);check(page.CardPresented,"preview action must reach the card presenter");
                page.ClientSize=new Size(560,700);Pump(100);check(Script(page,"document.documentElement.scrollWidth<=innerWidth")=="true","narrow settings must not overflow");
                Script(page,"Array.prototype.filter.call(document.querySelectorAll('[data-links] a'),function(a){return a.textContent==='免责声明';})[0].click()");Pump(100);
                check(Script(page,"document.body.getAttribute('data-view')==='intro' && document.querySelector('[data-intro-agree]').textContent==='完成' && document.querySelector('[data-intro-decline]').offsetParent===null && document.documentElement.scrollWidth<=innerWidth")=="true","footer link reopens the notice to reread, without the decline button or overflow");
                Script(page,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))");Pump(400);
                check(page.Visible && Script(page,"document.body.getAttribute('data-view')")=="\"settings\"","Escape leaves the reread notice, not the page");
                Script(page,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))");Pump(100);check(!page.Visible,"Escape closes preview settings");
                page.Close();
            }
        }
    }
}
