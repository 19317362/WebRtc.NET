﻿
namespace WebRtc.NET.AppLib
{
    using Fleck;
    using LitJson;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using System.Threading;
    using System.Net.Sockets;
    using System.Net;
    class WebRTCServer : IDisposable
    {
        class WebRtcSession
        {
            public readonly ManagedConductor WebRtc;
            public readonly CancellationTokenSource Cancel;

            public WebRtcSession()
            {
                WebRtc = new ManagedConductor();
                Cancel = new CancellationTokenSource();
            }            
        }

        ConcurrentDictionary<Guid, IWebSocketConnection> UserList = new ConcurrentDictionary<Guid, IWebSocketConnection>();
        ConcurrentDictionary<Guid, WebRtcSession> Streams = new ConcurrentDictionary<Guid, WebRtcSession>();

        WebSocketServer server;
        public WebRTCServer(int port) : this("ws://0.0.0.0:" + port)
        {
            
        }

        public WebRTCServer(string URL)
        {
            server = new WebSocketServer(URL);
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    try
                    {
                        OnConnected(socket);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OnConnected: {ex}");
                    }
                };
                socket.OnMessage = message =>
                {
                    try
                    {
                        OnReceive(socket, message);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OnReceive: {ex}");
                    }
                };
                socket.OnClose = () =>
                {
                    try
                    {
                        OnDisconnect(socket);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OnDisconnect: {ex}");
                    }
                };
                socket.OnError = (e) =>
                {
                    try
                    {
                        OnDisconnect(socket);
                        socket.Close();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OnError: {ex}");
                    }
                };
            });
        }

        private void OnConnected(IWebSocketConnection context)
        {
            if (UserList.Count < ClientLimit)
            {
                Debug.WriteLine($"OnConnected: {context.ConnectionInfo.Id}, {context.ConnectionInfo.ClientIpAddress}");

                UserList[context.ConnectionInfo.Id] = context;
            }
            else
            {
                Debug.WriteLine($"OverLimit, Closed: {context.ConnectionInfo.Id}, {context.ConnectionInfo.ClientIpAddress}");
                context.Close();
            }
        }

        private int clientLimit = 5; 
        public int ClientLimit
        {
            get
            {
                lock(this)
                {
                    return clientLimit;
                }
            }
            set
            {
                lock (this)
                {
                    clientLimit = value;
                }
            }
        }

        public int ClientCount
        {
            get
            {
                return UserList.Count;
            }
        }

        public int StreamsCount
        {
            get
            {
                return Streams.Count;
            }
        }

        private void OnDisconnect(IWebSocketConnection context)
        {
            Debug.WriteLine($"OnDisconnect: {context.ConnectionInfo.Id}, {context.ConnectionInfo.ClientIpAddress}");
            {
                IWebSocketConnection ctx;                
                UserList.TryRemove(context.ConnectionInfo.Id, out ctx);

                WebRtcSession s;
                if (Streams.TryRemove(context.ConnectionInfo.Id, out s))
                {
                    s.Cancel.Cancel();
                }
            }
        }

        public const string offer = "offer";
        public const string onicecandidate = "onicecandidate";

        private void OnReceive(IWebSocketConnection context, string msg)
        {
            Debug.WriteLine($"OnReceive {context.ConnectionInfo.Id}: {msg}");

            if (!msg.Contains("command")) return; 

            if(UserList.ContainsKey(context.ConnectionInfo.Id))
            {
                JsonData msgJson = JsonMapper.ToObject(msg);
                string command = msgJson["command"].ToString();

                switch (command) 
                {
                    case offer:
                    {
                        if (UserList.Count <= ClientLimit && !Streams.ContainsKey(context.ConnectionInfo.Id))
                        {
                            var session = Streams[context.ConnectionInfo.Id] = new WebRtcSession();
                            {
                                using (var go = new ManualResetEvent(false))
                                {
                                    var t = Task.Factory.StartNew(() =>
                                    {
                                        ManagedConductor.InitializeSSL();

                                        using (session.WebRtc)
                                        {
                                            session.WebRtc.AddServerConfig("stun:stun.l.google.com:19302", string.Empty, string.Empty);
                                            session.WebRtc.AddServerConfig("stun:stun.anyfirewall.com:3478", string.Empty, string.Empty);
                                            session.WebRtc.AddServerConfig("stun:stun.stunprotocol.org:3478", string.Empty, string.Empty);

                                            var ok = session.WebRtc.InitializePeerConnection();
                                            if (ok)
                                            {
                                                go.Set();

                                                while (!session.Cancel.Token.IsCancellationRequested &&
                                                       session.WebRtc.ProcessMessages(1000))
                                                {
                                                    Debug.Write(".");
                                                }
                                                session.WebRtc.ProcessMessages(1000);
                                            }
                                            else
                                            {
                                                Debug.WriteLine("InitializePeerConnection failed");
                                                context.Close();
                                            }
                                        }

                                        ManagedConductor.CleanupSSL();
                                    }, session.Cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                                    if (go.WaitOne(9999))
                                    {
                                        session.WebRtc.OnIceCandidate += delegate (string sdp_mid, int sdp_mline_index, string sdp)
                                        {
                                            if (context.IsAvailable)
                                            {
                                                JsonData j = new JsonData();
                                                j["command"] = "OnIceCandidate";
                                                j["sdp_mid"] = sdp_mid;
                                                j["sdp_mline_index"] = sdp_mline_index;
                                                j["sdp"] = sdp;
                                                context.Send(j.ToJson());
                                            }
                                        };

                                        session.WebRtc.OnSuccessAnswer += delegate(string sdp)
                                        {
                                            if (context.IsAvailable)
                                            {
                                                JsonData j = new JsonData();
                                                j["command"] = "OnSuccessAnswer";
                                                j["sdp"] = sdp;
                                                context.Send(j.ToJson());
                                            }
                                        };

                                        session.WebRtc.OnFailure += delegate(string error)
                                        {
                                            Trace.WriteLine($"OnFailure: {error}");
                                        };

                                        session.WebRtc.OnError += delegate
                                        {
                                            Trace.WriteLine("OnError");
                                        };

                                        unsafe
                                        {
                                            session.WebRtc.OnFillBuffer += delegate (byte * frame_buffer, long yuvSize)
                                            {
                                                OnFillBuffer(frame_buffer, yuvSize);
                                            };
                                        }

                                        var d = msgJson["desc"];
                                        var s = d["sdp"].ToString();

                                        session.WebRtc.OnOfferRequest(s);
                                    }
                                }
                            }
                        }
                    }
                    break;

                    case onicecandidate:
                    {
                        var c = msgJson["candidate"];

                        var sdpMLineIndex = (int)c["sdpMLineIndex"];
                        var sdpMid = c["sdpMid"].ToString();
                        var candidate = c["candidate"].ToString();

                        var session = Streams[context.ConnectionInfo.Id];
                        {
                            session.WebRtc.AddIceCandidate(sdpMid, sdpMLineIndex, candidate);
                        }
                    }
                    break;
                }
            }
        }

        public ManagedConductor.OnCallbackFillBuffer OnFillBuffer;

        public void Dispose()
        {
            try
            {
                foreach (IWebSocketConnection i in UserList.Values)
                {
                    i.Close();
                }
                server.Dispose();
                UserList.Clear();
            }
            catch { }
        }
    }
}
