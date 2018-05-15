using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GossipNet.Messages;
using Serilog;

namespace GossipNet.IO
{
    internal class GossipMessagePump : IGossipMessageReceiver, IGossipMessageSender, IDisposable
    {
        private readonly GossipNodeConfiguration _configuration;
        private readonly IGossipMessageDecoder _messageDecoder;
        private readonly IGossipMessageEncoder _messageEncoder;
        private BroadcastQueue _broadcasts;
        private GossipTcp _client;

        public GossipMessagePump(GossipNodeConfiguration configuration, IGossipMessageDecoder messageDecoder, IGossipMessageEncoder messageEncoder)
        {
            Debug.Assert(configuration != null);
            Debug.Assert(messageDecoder != null);
            Debug.Assert(messageEncoder != null);

            _configuration = configuration;
            _messageDecoder = messageDecoder;
            _messageEncoder = messageEncoder;
        }

        public event Action<IPEndPoint, GossipMessage> MessageReceived;

        public void Broadcast(BroadcastableMessage message, EventWaitHandle @event)
        {
            Debug.Assert(message != null);

            byte[] messageBytes;
            using (var ms = new MemoryStream())
            {
                _messageEncoder.Encode(message, ms);
                messageBytes = ms.ToArray();
            }

            var broadcast = new Broadcast(message, messageBytes, @event);
            _broadcasts.Enqueue(broadcast);
        }

        public void Dispose()
        {
            _client.Close();
        }

        public void Open(Func<int> broadcastTransmitCount)
        {
            Debug.Assert(broadcastTransmitCount != null);

            _broadcasts = new BroadcastQueue(broadcastTransmitCount);
            _client = new GossipTcp(_configuration.LocalEndPoint, DatagramReceived, _configuration.Logger);
        }

        public void Send(IPEndPoint remoteEndPoint, GossipMessage message)
        {
            Debug.Assert(remoteEndPoint != null);
            Debug.Assert(message != null);

            List<EventWaitHandle> eventsToTrigger = null;
            using (var ms = new MemoryStream())
            {
                try
                {
                    _configuration.Logger.Verbose("Sending {@Message} to {RemoteEndPoint}", message, remoteEndPoint);
                    _messageEncoder.Encode(message, ms);

                    List<byte[]> messageBytes = null;
                    foreach (var broadcast in _broadcasts.GetBroadcasts(0, 4096))
                    {
                        _configuration.Logger.Verbose("Sending {@Message} to {RemoteEndPoint}", broadcast.Message, remoteEndPoint);
                        if (messageBytes == null)
                        {
                            messageBytes = new List<byte[]>();
                            messageBytes.Add(ms.ToArray());
                            ms.SetLength(0);
                        }

                        messageBytes.Add(broadcast.MessageBytes);

                        if (broadcast.Event != null)
                        {
                            if (eventsToTrigger == null)
                            {
                                eventsToTrigger = new List<EventWaitHandle>();
                            }

                            eventsToTrigger.Add(broadcast.Event);
                        }
                    }

                    if (messageBytes != null)
                    {
                        message = new CompoundMessage(messageBytes);
                        ms.SetLength(0);
                        _messageEncoder.Encode(message, ms);
                    }

                    if (_configuration.CompressionType.HasValue)
                    {
                        _configuration.Logger.Verbose("Compressing {@Message} to {RemoteEndPoint} with {CompressionType}", message, remoteEndPoint, _configuration.CompressionType.Value);
                        message = new CompressedMessage(_configuration.CompressionType.Value, message);
                        ms.SetLength(0);
                        _messageEncoder.Encode(message, ms);
                    }
                }
                catch (Exception ex)
                {
                    _configuration.Logger.Error(ex, "Unable to send message(s) to {RemoteEndPoint}.", remoteEndPoint);
                    return;
                }

                _client.Send(remoteEndPoint, ms.ToArray());

                if (eventsToTrigger != null)
                {
                    foreach (var @event in eventsToTrigger)
                    {
                        @event.Set();
                    }
                }
            }
        }

        private void DatagramReceived(IPEndPoint remoteEndPoint, byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                try
                {
                    foreach (var message in _messageDecoder.Decode(ms))
                    {
                        if (MessageReceived != null)
                        {
                            try
                            {
                                MessageReceived(remoteEndPoint, message);
                            }
                            catch (Exception ex)
                            {
                                _configuration.Logger.Error(ex, "Error occured in MessageReceived handler for {Message} from {RemoteEndPoint}.", message, remoteEndPoint);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _configuration.Logger.Error(ex, "Unable to decode message from {RemoteEndPoint}.", remoteEndPoint);
                    return;
                }
            }
        }

        private class GossipUdpClient
        {
            private readonly UdpClient _client;
            private readonly ILogger _logger;
            private readonly Action<IPEndPoint, byte[]> _onReceive;
            private bool _closed;

            public GossipUdpClient(IPEndPoint localEndPoint, Action<IPEndPoint, byte[]> onReceive, ILogger logger)
            {
                Debug.Assert(localEndPoint != null);
                Debug.Assert(onReceive != null);
                Debug.Assert(logger != null);

                _client = new UdpClient();
                _client.ExclusiveAddressUse = false;
                _client.Client.Bind(localEndPoint);

                _onReceive = onReceive;
                _logger = logger;
                _client.BeginReceive(Receive, null);
            }

            public void Close()
            {
                _closed = true;
                _client.Close();
            }

            public void Send(IPEndPoint remoteEndPoint, byte[] bytes)
            {
                _client.Send(bytes, bytes.Length, remoteEndPoint);
            }

            private void Receive(IAsyncResult ar)
            {
                IPEndPoint remoteEndPoint = null;
                byte[] data = null;
                try
                {
                    data = _client.EndReceive(ar, ref remoteEndPoint);
                }
                catch (ObjectDisposedException)
                {
                    // thrown when the client is closed...
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error occured in udp client EndReceive. Ignoring and moving on.");
                }

                if (!_closed)
                {
                    _client.BeginReceive(Receive, null);
                    if (remoteEndPoint != null && data != null)
                    {
                        _onReceive(remoteEndPoint, data);
                    }
                }
            }
        }

        private class GossipTcp
        {
            private TcpListener tcpListener;
            private readonly ILogger _logger;
            private readonly Action<IPEndPoint, byte[]> _onReceive;
            private bool _closed;
            private Dictionary<string,NetworkStream> streams = new Dictionary<string,NetworkStream>();

            public GossipTcp(IPEndPoint localEndPoint, Action<IPEndPoint, byte[]> onReceive, ILogger logger)
            {
                Debug.Assert(localEndPoint != null);
                Debug.Assert(onReceive != null);
                Debug.Assert(logger != null);

                tcpListener = new TcpListener(localEndPoint);
                tcpListener.Start();
                Console.WriteLine("[Server] Listening on {0}", localEndPoint.ToString());
                StartListening();               

                _onReceive = onReceive;
                _logger = logger;
            }

            public async void StartListening()
            {
                TcpClient _client = await tcpListener.AcceptTcpClientAsync();
                var buffer = new byte[4096];
                StateObject state = new StateObject();
                state.workSocket = _client.Client;
                _client.Client.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0, Receive, state);              
                Console.WriteLine("[Server] Accepted Client");
            }

            public void Close()
            {
                _closed = true;
                tcpListener.Server.Close();
            }

            public async void Send(IPEndPoint remoteEndPoint, byte[] bytes)
            {
                try
                {
                    using (TcpClient _client = new TcpClient())
                    {
                        await _client.ConnectAsync(remoteEndPoint.Address, remoteEndPoint.Port);
                        using (NetworkStream networkStream = _client.GetStream())
                        {
                            networkStream.Write(bytes, 0, bytes.Length);
                        }
                    }
                }
                catch (Exception e)
                {
                    //Console.WriteLine(e.ToString());
                }
            }

            private void Receive(IAsyncResult ar)
            {
                StateObject state = (StateObject)ar.AsyncState;
                Socket handler = state.workSocket;
                IPEndPoint remoteEndPoint = handler.LocalEndPoint as IPEndPoint;

                byte[] data = null;
                try
                {
                    //data = _client.EndReceive(ar, ref remoteEndPoint);
                    int bytesRead = handler.EndReceive(ar);
                    if (bytesRead > 0)
                    {
                        state.sb.Append(Encoding.UTF8.GetString(state.buffer, 0, bytesRead));
                    }
                    data = state.buffer;
                }
                catch (ObjectDisposedException)
                {
                    // thrown when the client is closed...
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error occured in udp client EndReceive. Ignoring and moving on.");
                }

                if (!_closed)
                {
                    //_client.BeginReceive(Receive, null);
                    handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0, Receive, state);
                    if (remoteEndPoint != null && data != null)
                    {
                        _onReceive(remoteEndPoint, data);
                    }
                }
            }
        }

        // State object for reading client data asynchronously  
        public class StateObject
        {
            // Client  socket.  
            public Socket workSocket = null;
            // Size of receive buffer.  
            public const int BufferSize = 1024;
            // Receive buffer.  
            public byte[] buffer = new byte[BufferSize];
            // Received data string.  
            public StringBuilder sb = new StringBuilder();
        }
    }
}