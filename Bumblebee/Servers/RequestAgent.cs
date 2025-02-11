﻿using BeetleX.Buffers;
using BeetleX.Clients;
using BeetleX.FastHttpApi;
using System;
using System.Collections.Generic;
using System.Text;
using BeetleX;
using System.Net.Sockets;
using Bumblebee.Events;
using System.Threading.Tasks;
using System.Collections.Concurrent;
namespace Bumblebee.Servers
{
    public class RequestAgent
    {

        public RequestAgent(TcpClientAgent clientAgent, ServerAgent serverAgent, HttpRequest request, HttpResponse response,
            UrlRouteServerGroup.UrlServerInfo urlServerInfo, Routes.UrlRoute urlRoute)
        {
            mTransferEncoding = false;
            mRequestLength = 0;
            Code = 0;
            Server = serverAgent;
            Request = request;
            Response = response;
            mClientAgent = clientAgent;
            mClientAgent.Client.ClientError = OnSocketError;
            mClientAgent.Client.DataReceive = OnReveive;
            mBuffer = mClientAgent.Buffer;
            Status = RequestStatus.None;
            UrlServerInfo = urlServerInfo;
            UrlRoute = urlRoute;
            mStartTime = TimeWatch.GetElapsedMilliseconds();
            mRequestID = request.ID;
            //System.Threading.Interlocked.Increment(ref RequestCount);
            //mHistoryRequests[mRequestID] = this;
        }



        //public static long RequestCount;

        //public static ConcurrentDictionary<long, RequestAgent> mHistoryRequests = new ConcurrentDictionary<long, RequestAgent>();



        private Header mResponseHeader = new Header();

        public Header ResponseHeader => mResponseHeader;

        private long mStartTime;

        private long mRequestID;

        public long RequestID => mRequestID;

        public int BodyReceives { get; private set; } = 0;

        public string ResponseStatus { get; private set; }

        private byte[] mBuffer;

        private TcpClientAgent mClientAgent;

        private int mRequestLength;

        private bool mTransferEncoding = false;

        public TcpClientAgent ClientAgent => mClientAgent;

        public UrlRouteServerGroup.UrlServerInfo UrlServerInfo { get; private set; }

        public Routes.UrlRoute UrlRoute { get; set; }

        public HttpRequest Request { get; private set; }

        public long Time { get; set; }

        public HttpResponse Response { get; private set; }

        public ServerAgent Server { get; private set; }

        public int Code { get; set; }

        public RequestStatus Status { get; set; }

        public EventResponseErrorArgs ResponseError { get; set; }

        private void OnSocketError(IClient c, ClientErrorArgs e)
        {
            mClientAgent.Status = TcpClientAgentStatus.ResponseError;
            HttpApiServer httpApiServer = Server.Gateway.HttpServer;
            if (httpApiServer.EnableLog(BeetleX.EventArgs.LogType.Info))
                httpApiServer.Log(BeetleX.EventArgs.LogType.Error, $"gateway [{mRequestID}] request {Server.Host}:{Server.Port} error {e.Message}@{e.Error.InnerException?.Message} status {Status}");

            if (Status == RequestStatus.Requesting)
            {
                EventResponseErrorArgs erea;
                if (e.Error is SocketException)
                {
                    Code = Gateway.SERVER_SOCKET_ERROR;
                    erea = new EventResponseErrorArgs(Request, Response, UrlRoute.Gateway, e.Error.Message, Gateway.SERVER_SOCKET_ERROR);
                }
                else
                {
                    Code = Gateway.SERVER_PROCESS_ERROR_CODE;
                    erea = new EventResponseErrorArgs(Request, Response, UrlRoute.Gateway, e.Error.Message, Gateway.SERVER_PROCESS_ERROR_CODE);
                }
                OnCompleted(erea);
            }
            else
            {
                Code = Gateway.SERVER_OTHRER_ERROR_CODE;
                if (Status > RequestStatus.None)
                {
                    OnCompleted(null);
                }
            }

        }

        public PipeStream GetRequestStream()
        {
            return Request.Session.Stream.ToPipeStream();
        }

        private void OnReadResponseStatus(PipeStream pipeStream)
        {
            if (Status == RequestStatus.Responding)
            {
                var indexof = pipeStream.IndexOf(HeaderTypeFactory.LINE_BYTES);
                if (indexof.EofData != null)
                {
                    pipeStream.Read(mBuffer, 0, indexof.Length);
                    GetRequestStream().Write(mBuffer, 0, indexof.Length);
                    var result = HttpParse.AnalyzeResponseLine(new ReadOnlySpan<byte>(mBuffer, 0, indexof.Length - 2));
                    ResponseStatus = Encoding.ASCII.GetString(mBuffer, 0, indexof.Length - 2);
                    Code = result.Item2;
                    Status = RequestStatus.RespondingHeader;
                }
            }
        }

        private void OnReadResponseHeader(PipeStream pipeStream)
        {
            if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
            {
                Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"Gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} response stream reading");
            }
            PipeStream agentStream = GetRequestStream();
            if (Status == RequestStatus.RespondingHeader)
            {
                mClientAgent.Status = TcpClientAgentStatus.ResponseReciveHeader;
                var indexof = pipeStream.IndexOf(HeaderTypeFactory.LINE_BYTES);
                while (indexof.End != null)
                {
                    pipeStream.Read(mBuffer, 0, indexof.Length);

                    if (indexof.Length == 2)
                    {
                        if (Request.VersionNumber == "1.0" && Request.KeepAlive)
                        {
                            agentStream.Write(Gateway.KEEP_ALIVE, 0, Gateway.KEEP_ALIVE.Length);
                        }
                        mResponseHeader.Add(Gateway.GATEWAY_HEADER, Gateway.GATEWAY_VERSION);
                        UrlRoute.Pluginer.HeaderWriting(Request, Response, mResponseHeader);
                        mResponseHeader.Write(agentStream);
                        if (Server.Gateway.OutputServerAddress)
                        {
                            agentStream.Write("Logic-Server: " + Server.ServerName + "\r\n");
                        }
                        agentStream.Write(mBuffer, 0, indexof.Length);
                        Status = RequestStatus.RespondingBody;
                        if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                        {
                            Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} response stream read header ");
                        }
                        return;
                    }
                    else
                    {
                        var header = HttpParse.AnalyzeHeader(new ReadOnlySpan<byte>(mBuffer, 0, indexof.Length - 2));
                        if (string.Compare(header.Item1, HeaderTypeFactory.TRANSFER_ENCODING, true) == 0 && string.Compare(header.Item2, "chunked", true) == 0)
                        {
                            mTransferEncoding = true;
                        }
                        if (string.Compare(header.Item1, HeaderTypeFactory.CONTENT_LENGTH, true) == 0)
                        {
                            mRequestLength = int.Parse(header.Item2);
                        }
                        //if (string.Compare(header.Item1, HeaderTypeFactory.SERVER, true) == 0)
                        //{

                        //    mResponseHeader.Add(HeaderTypeFactory.SERVER, "Bumblebee(BeetleX)");
                        //}
                        //else
                        //{
                        mResponseHeader.Add(header.Item1, header.Item2);
                        //}
                    }
                    indexof = pipeStream.IndexOf(HeaderTypeFactory.LINE_BYTES);
                }
            }
        }

        private void OnReadResponseBody(PipeStream pipeStream)
        {

            PipeStream agentStream = GetRequestStream();
            if (Status == RequestStatus.RespondingBody)
            {
                mClientAgent.Status = TcpClientAgentStatus.ResponseReceiveBody;
                if (mTransferEncoding)
                {
                    while (pipeStream.Length > 0)
                    {
                        var len = pipeStream.Read(mBuffer, 0, mBuffer.Length);
                        BodyReceives++;
                        if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                        {
                            Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} response stream read size {len} ");
                        }
                        agentStream.Write(mBuffer, 0, len);
                        bool end = true;
                        for (int i = 0; i < 5; i++)
                        {
                            if (HeaderTypeFactory.CHUNKED_BYTES[i] != mBuffer[len - 5 + i])
                            {
                                end = false;
                                break;
                            }
                        }
                        if (end)
                        {
                            Server.Gateway.OnResponding(this, new ArraySegment<byte>(mBuffer, 0, len), true);
                            OnCompleted(null);
                            Request.Session.Stream.Flush();
                            return;
                        }
                        else
                        {
                            Server.Gateway.OnResponding(this, new ArraySegment<byte>(mBuffer, 0, len), false);
                            if (Request.KeepAlive && agentStream.CacheLength > 1024 * 2)
                                Request.Session.Stream.Flush();

                        }
                    }
                }
                else
                {
                    if (mRequestLength == 0)
                    {
                        OnCompleted(null);
                        Request.Session.Stream.Flush();
                        return;
                    }
                    while (pipeStream.Length > 0)
                    {
                        var len = 0;
                        if (mRequestLength > 0)
                        {
                            len = pipeStream.Read(mBuffer, 0, mBuffer.Length);
                            BodyReceives++;
                            if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                            {
                                Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} response stream read size {len} ");
                            }
                            mRequestLength -= len;
                            agentStream.Write(mBuffer, 0, len);
                        }
                        if (mRequestLength == 0)
                        {
                            Server.Gateway.OnResponding(this, new ArraySegment<byte>(mBuffer, 0, len), true);
                            OnCompleted(null);
                            Request.Session.Stream.Flush();
                            return;
                        }
                        else
                        {
                            Server.Gateway.OnResponding(this, new ArraySegment<byte>(mBuffer, 0, len), false);
                            if (Request.KeepAlive && agentStream.CacheLength > 1024 * 2)
                                Request.Session.Stream.Flush();
                        }
                    }
                }
            }
        }

        private void OnReveive(IClient c, ClientReceiveArgs reader)
        {
            mClientAgent.Status = TcpClientAgentStatus.ResponseReceive;
            PipeStream stream = reader.Stream.ToPipeStream();
            if (Status >= RequestStatus.Responding)
            {
                OnReadResponseStatus(stream);
                OnReadResponseHeader(stream);
                OnReadResponseBody(stream);
            }
            else
            {
                stream.ReadFree((int)stream.Length);
            }
        }

        public void Execute()
        {
            mClientAgent.Status = TcpClientAgentStatus.Requesting;
            var request = Request;
            var response = Response;
            Status = RequestStatus.Requesting;
            mClientAgent.Client.Connect();
            if (mClientAgent.Client.IsConnected)
            {
                try
                {
                    if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                    {
                        Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} request stream reading");
                    }
                    PipeStream pipeStream = mClientAgent.Client.Stream.ToPipeStream();
                    byte[] buffer = mBuffer;
                    int offset = 0;
                    var len = Encoding.UTF8.GetBytes(request.Method, 0, request.Method.Length, buffer, offset);
                    offset += len;

                    buffer[offset] = HeaderTypeFactory._SPACE_BYTE;
                    offset++;

                    len = Encoding.UTF8.GetBytes(request.Url, 0, request.Url.Length, buffer, offset);
                    offset += len;


                    buffer[offset] = HeaderTypeFactory._SPACE_BYTE;
                    offset++;

                    for (int i = 0; i < HeaderTypeFactory.HTTP_V11_BYTES.Length; i++)
                    {
                        buffer[offset + i] = HeaderTypeFactory.HTTP_V11_BYTES[i];
                    }
                    offset += HeaderTypeFactory.HTTP_V11_BYTES.Length;

                    buffer[offset] = HeaderTypeFactory._LINE_R;
                    offset++;

                    buffer[offset] = HeaderTypeFactory._LINE_N;
                    offset++;

                    pipeStream.Write(buffer, 0, offset);


                    request.Header.Write(pipeStream);
                    pipeStream.Write(HeaderTypeFactory.LINE_BYTES, 0, 2);
                    int bodylength = request.Length;
                    while (bodylength > 0)
                    {
                        len = request.Stream.Read(buffer, 0, buffer.Length);
                        if (len == 0)
                        {
                            if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Warring))
                            {
                                Request.Server.Log(BeetleX.EventArgs.LogType.Warring, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} request stream read error");
                            }
                            Code = Gateway.SERVER_NETWORK_READ_STREAM_ERROR;
                            EventResponseErrorArgs eventResponseErrorArgs =
                                new EventResponseErrorArgs(request, response, UrlRoute.Gateway, "read request stream error", Gateway.SERVER_SOCKET_ERROR);
                            OnCompleted(eventResponseErrorArgs);
                            return;
                        }
                        else
                        {
                            pipeStream.Write(buffer, 0, len);
                            bodylength -= len;
                            if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                            {
                                Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} request stream read size {len}");
                            }
                        }
                    }
                    Status = RequestStatus.Responding;
                    mClientAgent.Client.Stream.Flush();
                    if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                    {
                        Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} request stream read success");
                    }
                    mClientAgent.Status = TcpClientAgentStatus.RequestSuccess;
                }
                catch (Exception e_)
                {
                    mClientAgent.Status = TcpClientAgentStatus.RequestError;
                    string error = $" request to {Server.Host}:{Server.Port} error {e_.Message}";
                    EventResponseErrorArgs eventResponseErrorArgs =
                        new EventResponseErrorArgs(request, response, UrlRoute.Gateway, error, Gateway.SERVER_SOCKET_ERROR);
                    if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                    {
                        Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} request proxy stream write error {e_.Message}{e_.StackTrace}");
                    }
                    try
                    {
                        if (mClientAgent.Client != null && mClientAgent.Client.IsConnected)
                            mClientAgent.Client.DisConnect();
                    }
                    finally
                    {
                        OnCompleted(eventResponseErrorArgs);
                    }
                    return;
                }
            }
        }

        private int mCompletedStatus = 0;

        internal void Cancel()
        {
            if (System.Threading.Interlocked.CompareExchange(ref mCompletedStatus, 1, 0) == 0)
            {
                mClientAgent.Client.ClientError = null;
                mClientAgent.Client.DataReceive = null;
                if (mClientAgent.Client.IsConnected)
                    mClientAgent.Client.DisConnect();
                Server.Push(mClientAgent);
            }
        }
        internal void OnCompleted(EventResponseErrorArgs error)
        {
            if (System.Threading.Interlocked.CompareExchange(ref mCompletedStatus, 1, 0) == 0)
            {
                this.ResponseError = error;
                Time = (long)(TimeWatch.GetTotalMilliseconds() - Request.RequestTime);
                mClientAgent.Client.ClientError = null;
                mClientAgent.Client.DataReceive = null;
                mClientAgent.Status = TcpClientAgentStatus.ResponseSuccess;
                //System.Threading.Interlocked.Decrement(ref RequestCount);
                //mHistoryRequests.Remove(mRequestID, out RequestAgent value);
                try
                {
                    if (Code >= 500)
                    {
                        if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Warring))
                        {
                            Request.Server.Log(BeetleX.EventArgs.LogType.Warring, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} completed code {Code} use time:{Time}ms");
                        }
                    }
                    else
                    {
                        if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Info))
                        {
                            Request.Server.Log(BeetleX.EventArgs.LogType.Info, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} completed code {Code} use time:{Time}ms");
                        }
                    }
                    if (UrlRoute.Pluginer.RequestedEnabled)
                        UrlRoute.Pluginer.Requested(this.GetEventRequestCompletedArgs());
                    Completed?.Invoke(this);

                }
                catch (Exception e_)
                {
                    if (Request.Server.EnableLog(BeetleX.EventArgs.LogType.Error))
                    {
                        Request.Server.Log(BeetleX.EventArgs.LogType.Error, $"gateway {Request.ID} {Request.RemoteIPAddress} {Request.Method} {Request.Url} -> {Server.Host}:{Server.Port} completed event error {e_.Message}@{e_.StackTrace}");
                    }
                }
                finally
                {
                    Request.ClearStream();
                    if (error != null)
                    {
                        Server.Gateway.OnResponseError(error);
                    }
                    else
                        Request.Recovery();
                    Server.Push(mClientAgent);
                }

            }
        }

        public Action<RequestAgent> Completed { get; set; }

        public enum RequestStatus : int
        {
            None = 1,
            Requesting = 2,
            Responding = 8,
            RespondingHeader = 32,
            RespondingBody = 64
        }

        private EventRequestCompletedArgs eventRequestCompletedArgs;

        public EventRequestCompletedArgs GetEventRequestCompletedArgs()
        {
            if (eventRequestCompletedArgs.Gateway == null)
            {
                eventRequestCompletedArgs = new EventRequestCompletedArgs(
                  this.UrlRoute,
                  this.Request,
                  this.Response,
                  this.Server.Gateway,
                  this.Code,
                  this.Server,
                  this.Time,
                  this.Request.ID,
                  ResponseError != null ? ResponseError.Message : null
                  );
            }
            return eventRequestCompletedArgs;
        }
    }
}
