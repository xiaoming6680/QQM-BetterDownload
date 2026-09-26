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
                Script(page,"document.querySelector('[data-toggle]').click()");Pump(100);
                check(Script(page,"document.querySelector('[data-toggle]').getAttribute('aria-checked')==='false' && document.querySelector('[data-scan]').disabled")=="true","toggle and disabled scan must round-trip");
                Script(page,"document.querySelector('[data-toggle]').click();document.querySelector('[data-card-style] [data-value=compact]').click()");Pump(100);
                check(Script(page,"document.querySelector('[data-card-style] [data-value=compact]').getAttribute('aria-checked')==='true'")=="true","card style must round-trip");
                Script(page,"document.querySelector('[data-scan]').click()");Pump(100);
                check(Script(page,"document.querySelector('[data-scan-note]').textContent.indexOf('预览模式')>=0")=="true","scan action must show its result");
                Script(page,"document.querySelector('[data-preview]').click()");Pump(150);check(page.CardPresented,"preview action must reach the card presenter");
                page.ClientSize=new Size(560,700);Pump(100);check(Script(page,"document.documentElement.scrollWidth<=innerWidth")=="true","narrow settings must not overflow");
                Script(page,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))");Pump(100);check(!page.Visible,"Escape closes preview settings");
                page.Close();
            }
        }
    }
}
