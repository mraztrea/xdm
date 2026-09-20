using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using TraceLog;
using XDM.Core.BrowserMonitoring;

namespace XDM.Core
{
    public static class SingleInstance
    {
        public static Mutex GlobalMutex;
        public static void Ensure()
        {
            try
            {
                using var mutex = Mutex.OpenExisting(@"Global\XDM_Active_Instance");
                throw new InstanceAlreadyRunningException(@"XDM instance already running, Mutex exists 'Global\XDM_Active_Instance'");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Exception in NativeMessagingHostHandler ctor");
                if (ex is InstanceAlreadyRunningException)
                {
                    //exit successfully only when the running instance actually accepted our arguments,
                    //otherwise let the caller fall back to a normal launch
                    Environment.Exit(SendArgsToRunningInstance() ? 0 : 1);
                }
            }
            GlobalMutex = new Mutex(true, @"Global\XDM_Active_Instance");
        }

        private static bool SendArgsToRunningInstance()
        {
            var args = Environment.GetCommandLineArgs().Skip(1);
            var postData = JsonConvert.SerializeObject(args.Count() == 0 ? new string[] { "--restore-window" } : args);
            var data = Encoding.UTF8.GetBytes(postData);

            //the listener is started asynchronously, so a handoff during the first instance's startup
            //is refused at first: retry over ~1.5 s before giving up
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    Log.Debug("Sending to running instance...");
                    var request = WebRequest.Create("http://127.0.0.1:8597/args");
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.ContentLength = data.Length;
                    request.Timeout = 1000;
                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(data, 0, data.Length);
                    }
                    using (var response = request.GetResponse())
                    {
                        Log.Debug("Sent...");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed sending args to running instance");
                    Thread.Sleep(200);
                }
            }
            return false;
        }
    }

    public class InstanceAlreadyRunningException : Exception
    {
        public InstanceAlreadyRunningException(string message) : base(message)
        {
        }
    }
}
