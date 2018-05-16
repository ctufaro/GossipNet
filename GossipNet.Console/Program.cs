using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using System.Net.Sockets;

namespace GossipNet.Console
{
    class Program
    {
        private static string[] messages = new string[] { "Hi server", "I hear Laurel", "Yeah cray cray" };
        private static int count = 0;

        //https://gist.github.com/jamesmanning/2622054
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                //System.Console.WriteLine("Missing Command Arguments");
                StartClient();
            }
            else
            {
                switch (args[0].ToLower())
                {
                    case ("l"):
                        StartListener();
                        break;
                    case ("c"):
                        StartClient();
                        break;
                }
            }
            System.Console.ReadLine();
        }

        static void StartListener()
        {
            IPEndPoint ipEnd = new IPEndPoint(IPAddress.Loopback, 1234);
            

            List<TcpClient> peers = new List<TcpClient>();

            Task.Run(async() =>
            {
                TcpListener tcpListener = new TcpListener(ipEnd);
                tcpListener.Start();

                while (true)
                {
                    System.Console.WriteLine("[Server] Listening on {0}", ipEnd.ToString());
                    var tcpClient = await tcpListener.AcceptTcpClientAsync();                    
                    System.Console.WriteLine("[Server] Connection accepted from client '{0}'.", tcpClient.Client.RemoteEndPoint);
                    peers.Add(tcpClient);

                    await Task.Run(async() =>
                    {
                        System.Console.WriteLine("Number of Peers: " + peers.Count);
                        foreach(TcpClient p in peers)
                        {
                            NetworkStream ns = p.GetStream();
                            string ServerResponseString = DateTime.Now.ToString();
                            byte[] ServerResponseBytes = Encoding.UTF8.GetBytes(ServerResponseString);
                            await ns.WriteAsync(ServerResponseBytes, 0, ServerResponseBytes.Length);
                        }
                    });

                }
            });            
        }

        static async Task HandleClient(TcpClient tcpClient)
        {
            await Task.Run(async () =>
            {
                while (true)
                {
                    NetworkStream stream = tcpClient.GetStream();
                    var buffer = new byte[4096];
                    var byteCount = await stream.ReadAsync(buffer, 0, buffer.Length);
                    var request = Encoding.UTF8.GetString(buffer, 0, byteCount);
                    System.Console.WriteLine("[Server] Client wrote {0}", request);
                }
            });
        }

        static async void StartClient()
        {
            var tcpClient = new TcpClient();
            System.Console.WriteLine("[Client] Connecting to server");
            await tcpClient.ConnectAsync("127.0.0.1", 1234);
            System.Console.WriteLine("[Client] Connected to server, opening stream");

            await Task.Run(async () =>
            {
                NetworkStream stream = tcpClient.GetStream();
                while (true)
                {
                    await SendClientMessage(stream);
                    Thread.Sleep(2000);
                    await ReceiveClientMessage(stream);
                    Thread.Sleep(2000);
                }
            });
        }

        private static async Task ReceiveClientMessage(NetworkStream networkStream)
        {
            var buffer = new byte[4096];
            var byteCount = await networkStream.ReadAsync(buffer, 0, buffer.Length);
            var request = Encoding.UTF8.GetString(buffer, 0, byteCount);
            System.Console.WriteLine("[Server] Client wrote {0}", request);
        }

        private static async Task SendClientMessage(NetworkStream networkStream)
        {
            if (count < messages.Length)
            {
                string response = messages[count];
                count = Interlocked.Increment(ref count);
                byte[] serverResponseBytes = Encoding.UTF8.GetBytes(response);
                await networkStream.WriteAsync(serverResponseBytes, 0, serverResponseBytes.Length);
            }
        }
        static void Main2(string[] args)
        {
            if(Debugger.IsAttached)
            {
                // running inside VS
                args = new[] { "20000" };
            }

            if (args.Length == 0)
            {
                args = new[] { "30000", "20000" };
            }

            var port = int.Parse(args[0]);
            int? joinPort = null;
            if(args.Length == 2)
            {
                joinPort = int.Parse(args[1]);
            }

            var config = GossipNodeConfiguration.Create(x =>
            {
                x.LocalEndPoint = new IPEndPoint(IPAddress.Loopback, port);
                x.LoggerConfiguration = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.ColoredConsole();
            });

            var node = new LocalGossipNode(config);

            node.NodeJoined += n => config.Logger.Information("{Name} joined cluster.", n.Name, n.IPEndPoint);
            node.NodeLeft += n => config.Logger.Information("{Name} left cluster.", n.Name, n.IPEndPoint);

            if(joinPort != null)
            {
                node.JoinCluster(new IPEndPoint(IPAddress.Loopback, joinPort.Value));
            }

            System.Console.ReadKey();
            node.LeaveCluster(Timeout.InfiniteTimeSpan);
            node.Dispose();
        }
    }
}