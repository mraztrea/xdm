using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Timers;
using System.Threading.Tasks;
using Gtk;
using XDM.Core;
using TraceLog;

namespace XDM.GtkUI
{
    public class PollingClipboardMonitor : IPlatformClipboardMonitor
    {
        private Timer timer;
        private string lastText;
        private Clipboard cb;
        private bool requestPending;
        public PollingClipboardMonitor()
        {
            cb = Clipboard.Get(Gdk.Selection.Clipboard);
            timer = new Timer(1000);
            timer.Elapsed += Timer_Elapsed;
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            Gtk.Application.Invoke(this.CheckGtkClipboardContents);
        }

        private void CheckGtkClipboardContents(object? sender, EventArgs e)
        {
            if (cb == null)
            {
                Log.Debug("Clipboard is null");
                return;
            }
            //WaitForText() spins a nested main loop and blocks the UI (and the tray menu) while the
            //clipboard owner is slow; ask asynchronously and skip polls while a request is in flight
            if (requestPending) return;
            requestPending = true;
            cb.RequestText((_, text) =>
            {
                requestPending = false;
                if (text != lastText)
                {
                    Log.Debug("Clipboard changed");
                    lastText = text;
                    this.ClipboardChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        public event EventHandler? ClipboardChanged;

        public string GetClipboardText() => lastText;

        public void StartClipboardMonitoring()
        {
            timer.Start();
        }

        public void StopClipboardMonitoring()
        {
            timer.Stop();
        }
    }
}
