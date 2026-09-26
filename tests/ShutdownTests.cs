using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace QqmBetterDownload {
    // WebView2 is limited to design previews. Retain the disposed-controller
    // regression for that path; QQ uses the separate GF lifetime instead.
    internal static class ShutdownTests {
        static void Pump(int ms) { var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);} }
        static void Until(Func<bool> ready) { var clock=Stopwatch.StartNew();while(!ready()){if(clock.ElapsedMilliseconds>20000)throw new Exception("Preview shutdown timed out.");Pump(20);} }
        internal static void Run(Action<bool,string> check,string folder) {
            var errors=new List<Exception>();ThreadExceptionEventHandler onError=(sender,e)=>errors.Add(e.Exception);Application.ThreadException+=onError;
            try {
                foreach(string phase in new[]{"initializing","loaded","controller-closed"})using(var page=new AppWindow(true)) {
                    page.ShowInTaskbar=false;page.StartPosition=FormStartPosition.Manual;page.Location=new Point(-10000,-10000);page.Restore();
                    if(phase!="initializing") {
                        Until(()=>page.HtmlReady||page.HtmlError.Length>0);check(page.HtmlReady,"preview fixture must load HTML");
                        page.PreviewCard();page.BeginInvoke((Action)page.PreparePreview);
                        if(phase=="controller-closed") {
                            var field=typeof(HtmlHost).GetField("controller",BindingFlags.Instance|BindingFlags.NonPublic);
                            check(field!=null,"preview controller available for fault injection");
                            ((CoreWebView2Controller)field.GetValue(page.Controls.OfType<HtmlHost>().Single())).Close();page.PreparePreview();
                        }
                    }
                    page.Close();Pump(700);
                    check(errors.Count==0,"preview close must not access a disposed browser");check(page.IsDisposed,"preview must dispose: "+phase);
                    page.PreparePreview();page.PreviewCard();check(!page.CardPresented,"late updates cannot reopen disposed UI: "+phase);
                }
            } finally { Application.ThreadException-=onError; }
        }
    }
}
