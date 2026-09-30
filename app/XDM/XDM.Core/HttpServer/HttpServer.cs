using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TraceLog;

namespace XDM.Core.HttpServer
{
    public class NanoServer
    {
        private readonly TcpListener listener;
        private volatile bool stopped;
        public event EventHandler<RequestContextEventArgs>? RequestReceived;

        public NanoServer(int port) : this(IPAddress.Any, port) { }

        public NanoServer(IPAddress host, int port)
        {
            this.listener = new TcpListener(host, port);
        }

        public void Start()
        {
            listener.Start();
            while (!stopped)
            {
                TcpClient tcp;
                try
                {
                    tcp = listener.AcceptTcpClient();
                }
                catch (Exception ex) when (!stopped && (ex is SocketException || ex is IOException))
                {
                    //a failed accept (aborted connection, out of descriptors) must not end browser integration
                    Log.Debug(ex, "AcceptTcpClient");
                    Thread.Sleep(100);
                    continue;
                }
                ProcessRequest(tcp);
            }
        }

        public void Stop()
        {
            stopped = true;
            try
            {
                this.listener.Stop();
            }
            catch { }
        }

        private void ProcessRequest(TcpClient tcp)
        {
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var ctx = HttpParser.ParseContext(tcp);
                        this.RequestReceived?.Invoke(this, new RequestContextEventArgs(ctx));
                        if (!ctx.KeepAlive)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, ex.Message);
                }
                finally
                {
                    try { tcp.Close(); } catch { }
                }
            }).Start();
        }
    }
}
