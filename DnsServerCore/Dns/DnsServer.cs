﻿/*
Technitium DNS Server
Copyright (C) 2020  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.ZoneManagers;
using DnsServerCore.Dns.Zones;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;
using TechnitiumLibrary.Net.Http;
using TechnitiumLibrary.Net.Proxy;

namespace DnsServerCore.Dns
{
    public sealed class DnsServer : IDisposable
    {
        #region enum

        enum ServiceState
        {
            Stopped = 0,
            Starting = 1,
            Running = 2,
            Stopping = 3
        }

        #endregion

        #region variables

        const int MAX_CNAME_HOPS = 16;
        const int SERVE_STALE_WAIT_TIME = 1800;

        string _serverDomain;
        readonly string _configFolder;
        readonly string _dohwwwFolder;
        IReadOnlyList<IPEndPoint> _localEndPoints;
        LogManager _log;

        NameServerAddress _thisServer;

        readonly List<Socket> _udpListeners = new List<Socket>();
        readonly List<Socket> _tcpListeners = new List<Socket>();
        readonly List<Socket> _httpListeners = new List<Socket>();
        readonly List<Socket> _tlsListeners = new List<Socket>();
        readonly List<Socket> _httpsListeners = new List<Socket>();

        bool _enableDnsOverHttp;
        bool _enableDnsOverTls;
        bool _enableDnsOverHttps;
        bool _isDnsOverHttpsEnabled;
        X509Certificate2 _certificate;

        readonly AuthZoneManager _authZoneManager;
        readonly AllowedZoneManager _allowedZoneManager;
        readonly BlockedZoneManager _blockedZoneManager;
        readonly BlockListZoneManager _blockListZoneManager;
        readonly CacheZoneManager _cacheZoneManager = new CacheZoneManager();

        readonly ResolverDnsCache _dnsCache;

        readonly DnsARecord _aRecord = new DnsARecord(IPAddress.Any);
        readonly DnsAAAARecord _aaaaRecord = new DnsAAAARecord(IPAddress.IPv6Any);

        bool _allowRecursion;
        bool _allowRecursionOnlyForPrivateNetworks;
        NetProxy _proxy;
        IReadOnlyList<NameServerAddress> _forwarders;
        bool _preferIPv6;
        bool _randomizeName;
        int _forwarderRetries = 3;
        int _resolverRetries = 5;
        int _forwarderTimeout = 4000;
        int _resolverTimeout = 4000;
        int _clientTimeout = 4000;
        int _forwarderConcurrency = 2;
        int _resolverMaxStackCount = 10;
        bool _serveStale = true;
        int _cachePrefetchEligibility = 2;
        int _cachePrefetchTrigger = 9;
        int _cachePrefetchSampleIntervalInMinutes = 5;
        int _cachePrefetchSampleEligibilityHitsPerHour = 30;
        LogManager _queryLog;
        readonly StatsManager _stats;

        int _tcpSendTimeout = 10000;
        int _tcpReceiveTimeout = 10000;

        Timer _cachePrefetchSamplingTimer;
        readonly object _cachePrefetchSamplingTimerLock = new object();

        Timer _cachePrefetchRefreshTimer;
        readonly object _cachePrefetchRefreshTimerLock = new object();
        const int CACHE_PREFETCH_REFRESH_TIMER_INITIAL_INTEVAL = 60000;
        DateTime _cachePrefetchSamplingTimerTriggersOn;
        IList<CacheRefreshSample> _cacheRefreshSampleList;

        Timer _cacheMaintenanceTimer;
        const int CACHE_MAINTENANCE_TIMER_INITIAL_INTEVAL = 60 * 60 * 1000;
        const int CACHE_MAINTENANCE_TIMER_PERIODIC_INTERVAL = 60 * 60 * 1000;

        readonly IndependentTaskScheduler _resolverTaskScheduler = new IndependentTaskScheduler(ThreadPriority.AboveNormal);
        readonly DomainTree<Task<DnsDatagram>> _resolverTasks = new DomainTree<Task<DnsDatagram>>();

        volatile ServiceState _state = ServiceState.Stopped;

        #endregion

        #region constructor

        static DnsServer()
        {
            //set min threads since the default value is too small
            {
                ThreadPool.GetMinThreads(out int minWorker, out int minIOC);

                int minThreads = Environment.ProcessorCount * 256;

                if (minWorker < minThreads)
                    minWorker = minThreads;

                if (minIOC < minThreads)
                    minIOC = minThreads;

                ThreadPool.SetMinThreads(minWorker, minIOC);
            }

            if (ServicePointManager.DefaultConnectionLimit < 10)
                ServicePointManager.DefaultConnectionLimit = 10; //concurrent http request limit required when using DNS-over-HTTPS forwarders
        }

        public DnsServer(string serverDomain, string configFolder, string dohwwwFolder, LogManager log = null)
            : this(serverDomain, configFolder, dohwwwFolder, new IPEndPoint[] { new IPEndPoint(IPAddress.Any, 53), new IPEndPoint(IPAddress.IPv6Any, 53) }, log)
        { }

        public DnsServer(string serverDomain, string configFolder, string dohwwwFolder, IPEndPoint localEndPoint, LogManager log = null)
            : this(serverDomain, configFolder, dohwwwFolder, new IPEndPoint[] { localEndPoint }, log)
        { }

        public DnsServer(string serverDomain, string configFolder, string dohwwwFolder, IReadOnlyList<IPEndPoint> localEndPoints, LogManager log = null)
        {
            _serverDomain = serverDomain;
            _configFolder = configFolder;
            _dohwwwFolder = dohwwwFolder;
            _localEndPoints = localEndPoints;
            _log = log;

            _authZoneManager = new AuthZoneManager(this);
            _allowedZoneManager = new AllowedZoneManager(this);
            _blockedZoneManager = new BlockedZoneManager(this);
            _blockListZoneManager = new BlockListZoneManager(this);

            _dnsCache = new ResolverDnsCache(_authZoneManager, _cacheZoneManager);

            //init stats
            _stats = new StatsManager(this);
        }

        #endregion

        #region IDisposable

        bool _disposed;

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                Stop();

                if (_authZoneManager != null)
                    _authZoneManager.Dispose();

                if (_stats != null)
                    _stats.Dispose();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region private

        private async Task ReadUdpRequestAsync(Socket udpListener)
        {
            byte[] recvBuffer = new byte[512];

            try
            {
                UdpReceiveFromResult result;

                while (true)
                {
                    try
                    {
                        result = await udpListener.ReceiveFromAsync(recvBuffer);
                    }
                    catch (SocketException ex)
                    {
                        switch (ex.SocketErrorCode)
                        {
                            case SocketError.ConnectionReset:
                            case SocketError.HostUnreachable:
                            case SocketError.MessageSize:
                            case SocketError.NetworkReset:
                                result = null;
                                break;

                            default:
                                throw;
                        }
                    }

                    if ((result != null) && (result.BytesReceived > 0))
                    {
                        try
                        {
                            DnsDatagram request = DnsDatagram.ReadFromUdp(new MemoryStream(recvBuffer, 0, result.BytesReceived, false));

                            _ = ProcessUdpRequestAsync(udpListener, result.RemoteEndPoint as IPEndPoint, request);
                        }
                        catch (Exception ex)
                        {
                            LogManager log = _log;
                            if (log != null)
                                log.Write(result.RemoteEndPoint as IPEndPoint, DnsTransportProtocol.Udp, ex);
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                //server stopping
            }
            catch (Exception ex)
            {
                if ((_state == ServiceState.Stopping) || (_state == ServiceState.Stopped))
                    return; //server stopping

                LogManager log = _log;
                if (log != null)
                    log.Write(ex);
            }
        }

        private async Task ProcessUdpRequestAsync(Socket udpListener, IPEndPoint remoteEP, DnsDatagram request)
        {
            try
            {
                DnsDatagram response;

                if (request.ParsingException == null)
                {
                    response = await ProcessQueryAsync(request, remoteEP, IsRecursionAllowed(remoteEP), DnsTransportProtocol.Udp);
                }
                else
                {
                    //format error
                    LogManager log = _log;
                    if (log != null)
                        log.Write(remoteEP, DnsTransportProtocol.Udp, request.ParsingException);

                    //format error response
                    response = new DnsDatagram(request.Identifier, true, request.OPCODE, false, false, request.RecursionDesired, IsRecursionAllowed(remoteEP), false, false, DnsResponseCode.FormatError, request.Question);
                }

                //send response
                if (response != null)
                {
                    byte[] sendBuffer = new byte[512];
                    MemoryStream sendBufferStream = new MemoryStream(sendBuffer);

                    try
                    {
                        response.WriteToUdp(sendBufferStream);
                    }
                    catch (NotSupportedException)
                    {
                        response = new DnsDatagram(response.Identifier, true, response.OPCODE, response.AuthoritativeAnswer, true, response.RecursionDesired, response.RecursionAvailable, response.AuthenticData, response.CheckingDisabled, response.RCODE, response.Question);

                        sendBufferStream.Position = 0;
                        response.WriteToUdp(sendBufferStream);
                    }

                    //send dns datagram async
                    await udpListener.SendToAsync(sendBuffer, 0, (int)sendBufferStream.Position, remoteEP);

                    LogManager queryLog = _queryLog;
                    if (queryLog != null)
                        queryLog.Write(remoteEP, DnsTransportProtocol.Udp, request, response);

                    StatsManager stats = _stats;
                    if (stats != null)
                        stats.Update(response, remoteEP.Address);
                }
            }
            catch (Exception ex)
            {
                if ((_state == ServiceState.Stopping) || (_state == ServiceState.Stopped))
                    return; //server stopping

                LogManager queryLog = _queryLog;
                if (queryLog != null)
                    queryLog.Write(remoteEP, DnsTransportProtocol.Udp, request, null);

                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP, DnsTransportProtocol.Udp, ex);
            }
        }

        private async Task AcceptConnectionAsync(Socket tcpListener, DnsTransportProtocol protocol, bool usingHttps)
        {
            IPEndPoint localEP = tcpListener.LocalEndPoint as IPEndPoint;

            try
            {
                tcpListener.SendTimeout = _tcpSendTimeout;
                tcpListener.ReceiveTimeout = _tcpReceiveTimeout;
                tcpListener.NoDelay = true;

                while (true)
                {
                    Socket socket = await tcpListener.AcceptAsync();

                    _ = ProcessConnectionAsync(socket, protocol, usingHttps);
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.OperationAborted)
                    return; //server stopping

                LogManager log = _log;
                if (log != null)
                    log.Write(localEP, protocol, ex);
            }
            catch (ObjectDisposedException)
            {
                //server stopped
            }
            catch (Exception ex)
            {
                if ((_state == ServiceState.Stopping) || (_state == ServiceState.Stopped))
                    return; //server stopping

                LogManager log = _log;
                if (log != null)
                    log.Write(localEP, protocol, ex);
            }
        }

        private async Task ProcessConnectionAsync(Socket socket, DnsTransportProtocol protocol, bool usingHttps)
        {
            IPEndPoint remoteEP = null;

            try
            {
                remoteEP = socket.RemoteEndPoint as IPEndPoint;

                switch (protocol)
                {
                    case DnsTransportProtocol.Tcp:
                        await ReadStreamRequestAsync(new NetworkStream(socket), _tcpReceiveTimeout, remoteEP, protocol);
                        break;

                    case DnsTransportProtocol.Tls:
                        SslStream tlsStream = new SslStream(new NetworkStream(socket));
                        await tlsStream.AuthenticateAsServerAsync(_certificate);

                        await ReadStreamRequestAsync(tlsStream, _tcpReceiveTimeout, remoteEP, protocol);
                        break;

                    case DnsTransportProtocol.Https:
                        Stream stream = new NetworkStream(socket);

                        if (usingHttps)
                        {
                            SslStream httpsStream = new SslStream(stream);
                            await httpsStream.AuthenticateAsServerAsync(_certificate);

                            stream = httpsStream;
                        }

                        await ProcessDoHRequestAsync(stream, _tcpReceiveTimeout, remoteEP, usingHttps);
                        break;
                }
            }
            catch (IOException)
            {
                //ignore IO exceptions
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP, protocol, ex);
            }
            finally
            {
                if (socket != null)
                    socket.Dispose();
            }
        }

        private async Task ReadStreamRequestAsync(Stream stream, int receiveTimeout, IPEndPoint remoteEP, DnsTransportProtocol protocol)
        {
            try
            {
                MemoryStream readBuffer = new MemoryStream(64);
                MemoryStream writeBuffer = new MemoryStream(64);
                SemaphoreSlim writeSemaphore = new SemaphoreSlim(1, 1);

                while (true)
                {
                    DnsDatagram request;

                    //read dns datagram with timeout
                    using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource())
                    {
                        Task<DnsDatagram> task = DnsDatagram.ReadFromTcpAsync(stream, readBuffer, cancellationTokenSource.Token);

                        if (await Task.WhenAny(task, Task.Delay(receiveTimeout, cancellationTokenSource.Token)) != task)
                        {
                            //read timed out
                            stream.Dispose();
                            return;
                        }

                        cancellationTokenSource.Cancel(); //cancel delay task

                        request = await task;
                    }

                    //process request async
                    _ = ProcessStreamRequestAsync(stream, writeBuffer, writeSemaphore, remoteEP, request, protocol);
                }
            }
            catch (ObjectDisposedException)
            {
                //ignore
            }
            catch (IOException)
            {
                //ignore IO exceptions
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP, protocol, ex);
            }
        }

        private async Task ProcessStreamRequestAsync(Stream stream, MemoryStream writeBuffer, SemaphoreSlim writeSemaphore, IPEndPoint remoteEP, DnsDatagram request, DnsTransportProtocol protocol)
        {
            try
            {
                DnsDatagram response;

                if (request.ParsingException == null)
                {
                    response = await ProcessQueryAsync(request, remoteEP, IsRecursionAllowed(remoteEP), protocol);
                }
                else
                {
                    //format error
                    response = new DnsDatagram(request.Identifier, true, request.OPCODE, false, false, request.RecursionDesired, IsRecursionAllowed(remoteEP), false, false, DnsResponseCode.FormatError, request.Question);

                    LogManager queryLog = _queryLog;
                    if (queryLog != null)
                        queryLog.Write(remoteEP, protocol, request, response);

                    LogManager log = _log;
                    if (log != null)
                        log.Write(remoteEP, protocol, request.ParsingException);
                }

                //send response
                if (response != null)
                {
                    await writeSemaphore.WaitAsync();
                    try
                    {
                        //send dns datagram
                        await response.WriteToTcpAsync(stream, writeBuffer);
                        await stream.FlushAsync();
                    }
                    finally
                    {
                        writeSemaphore.Release();
                    }

                    LogManager queryLog = _queryLog;
                    if (queryLog != null)
                        queryLog.Write(remoteEP, protocol, request, response);

                    StatsManager stats = _stats;
                    if (stats != null)
                        stats.Update(response, remoteEP.Address);
                }
            }
            catch (IOException)
            {
                //ignore IO exceptions
            }
            catch (Exception ex)
            {
                LogManager queryLog = _queryLog;
                if ((queryLog != null) && (request != null))
                    queryLog.Write(remoteEP, protocol, request, null);

                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP, protocol, ex);
            }
        }

        private async Task ProcessDoHRequestAsync(Stream stream, int receiveTimeout, IPEndPoint remoteEP, bool usingHttps)
        {
            DnsDatagram dnsRequest = null;
            DnsTransportProtocol dnsProtocol = DnsTransportProtocol.Https;

            try
            {
                while (true)
                {
                    HttpRequest httpRequest = await HttpRequest.ReadRequestAsync(stream).WithTimeout(receiveTimeout);
                    if (httpRequest == null)
                        return; //connection closed gracefully by client

                    IPEndPoint socketRemoteEP = remoteEP;

                    if (!usingHttps && NetUtilities.IsPrivateIP(socketRemoteEP.Address))
                    {
                        string xRealIp = httpRequest.Headers["X-Real-IP"];
                        if (IPAddress.TryParse(xRealIp, out IPAddress address))
                        {
                            //get the real IP address of the requesting client from X-Real-IP header set in nginx proxy_pass block
                            remoteEP = new IPEndPoint(address, 0);
                        }
                    }

                    string requestConnection = httpRequest.Headers[HttpRequestHeader.Connection];
                    if (string.IsNullOrEmpty(requestConnection))
                        requestConnection = "close";

                    switch (httpRequest.RequestPath)
                    {
                        case "/dns-query":
                            if (!usingHttps && !NetUtilities.IsPrivateIP(socketRemoteEP.Address))
                            {
                                //intentionally blocking public IP addresses from using DNS-over-HTTP (without TLS)
                                //this feature is intended to be used with an SSL terminated reverse proxy like nginx on private network
                                await SendErrorAsync(stream, 403, "DNS-over-HTTPS (DoH) queries are supported only on HTTPS.");
                                return;
                            }

                            DnsTransportProtocol protocol = DnsTransportProtocol.Udp;

                            string strRequestAcceptTypes = httpRequest.Headers[HttpRequestHeader.Accept];
                            if (!string.IsNullOrEmpty(strRequestAcceptTypes))
                            {
                                foreach (string acceptType in strRequestAcceptTypes.Split(','))
                                {
                                    if (acceptType == "application/dns-message")
                                    {
                                        protocol = DnsTransportProtocol.Https;
                                        break;
                                    }
                                    else if (acceptType == "application/dns-json")
                                    {
                                        protocol = DnsTransportProtocol.HttpsJson;
                                        dnsProtocol = DnsTransportProtocol.HttpsJson;
                                        break;
                                    }
                                }
                            }

                            switch (protocol)
                            {
                                case DnsTransportProtocol.Https:
                                    #region https wire format
                                    {
                                        switch (httpRequest.HttpMethod)
                                        {
                                            case "GET":
                                                string strRequest = httpRequest.QueryString["dns"];
                                                if (string.IsNullOrEmpty(strRequest))
                                                    throw new ArgumentNullException("dns");

                                                //convert from base64url to base64
                                                strRequest = strRequest.Replace('-', '+');
                                                strRequest = strRequest.Replace('_', '/');

                                                //add padding
                                                int x = strRequest.Length % 4;
                                                if (x > 0)
                                                    strRequest = strRequest.PadRight(strRequest.Length - x + 4, '=');

                                                dnsRequest = DnsDatagram.ReadFromUdp(new MemoryStream(Convert.FromBase64String(strRequest)));
                                                break;

                                            case "POST":
                                                string strContentType = httpRequest.Headers[HttpRequestHeader.ContentType];
                                                if (string.IsNullOrEmpty(strContentType))
                                                    throw new DnsServerException("Missing Content-Type header.");

                                                if (strContentType != "application/dns-message")
                                                    throw new NotSupportedException("DNS request type not supported: " + strContentType);

                                                using (MemoryStream mS = new MemoryStream())
                                                {
                                                    await httpRequest.InputStream.CopyToAsync(mS, 512);

                                                    mS.Position = 0;
                                                    dnsRequest = DnsDatagram.ReadFromUdp(mS);
                                                }

                                                break;

                                            default:
                                                throw new NotSupportedException("DoH request type not supported.");
                                        }

                                        DnsDatagram dnsResponse;

                                        if (dnsRequest.ParsingException == null)
                                        {
                                            dnsResponse = await ProcessQueryAsync(dnsRequest, remoteEP, IsRecursionAllowed(remoteEP), protocol);
                                        }
                                        else
                                        {
                                            //format error
                                            LogManager log = _log;
                                            if (log != null)
                                                log.Write(remoteEP, protocol, dnsRequest.ParsingException);

                                            //format error response
                                            dnsResponse = new DnsDatagram(dnsRequest.Identifier, true, dnsRequest.OPCODE, false, false, dnsRequest.RecursionDesired, IsRecursionAllowed(remoteEP), false, false, DnsResponseCode.FormatError, dnsRequest.Question);
                                        }

                                        if (dnsResponse != null)
                                        {
                                            using (MemoryStream mS = new MemoryStream())
                                            {
                                                dnsResponse.WriteToUdp(mS);

                                                byte[] buffer = mS.ToArray();
                                                await SendContentAsync(stream, "application/dns-message", buffer);
                                            }

                                            LogManager queryLog = _queryLog;
                                            if (queryLog != null)
                                                queryLog.Write(remoteEP, protocol, dnsRequest, dnsResponse);

                                            StatsManager stats = _stats;
                                            if (stats != null)
                                                stats.Update(dnsResponse, remoteEP.Address);
                                        }
                                    }
                                    #endregion
                                    break;

                                case DnsTransportProtocol.HttpsJson:
                                    #region https json format
                                    {
                                        string strName = httpRequest.QueryString["name"];
                                        if (string.IsNullOrEmpty(strName))
                                            throw new ArgumentNullException("name");

                                        string strType = httpRequest.QueryString["type"];
                                        if (string.IsNullOrEmpty(strType))
                                            strType = "1";

                                        dnsRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(strName, (DnsResourceRecordType)int.Parse(strType), DnsClass.IN) });

                                        DnsDatagram dnsResponse = await ProcessQueryAsync(dnsRequest, remoteEP, IsRecursionAllowed(remoteEP), protocol);
                                        if (dnsResponse != null)
                                        {
                                            using (MemoryStream mS = new MemoryStream())
                                            {
                                                JsonTextWriter jsonWriter = new JsonTextWriter(new StreamWriter(mS));
                                                dnsResponse.WriteToJson(jsonWriter);
                                                jsonWriter.Flush();

                                                byte[] buffer = mS.ToArray();
                                                await SendContentAsync(stream, "application/dns-json; charset=utf-8", buffer);
                                            }

                                            LogManager queryLog = _queryLog;
                                            if (queryLog != null)
                                                queryLog.Write(remoteEP, protocol, dnsRequest, dnsResponse);

                                            StatsManager stats = _stats;
                                            if (stats != null)
                                                stats.Update(dnsResponse, remoteEP.Address);
                                        }
                                    }
                                    #endregion
                                    break;

                                default:
                                    await SendErrorAsync(stream, 406, "Only application/dns-message and application/dns-json types are accepted.");
                                    break;
                            }

                            if (requestConnection.Equals("close", StringComparison.OrdinalIgnoreCase))
                                return;

                            break;

                        default:
                            string path = httpRequest.RequestPath;

                            if (!path.StartsWith("/") || path.Contains("/../") || path.Contains("/.../"))
                            {
                                await SendErrorAsync(stream, 404);
                                break;
                            }

                            if (path == "/")
                                path = "/index.html";

                            path = Path.GetFullPath(_dohwwwFolder + path.Replace('/', Path.DirectorySeparatorChar));

                            if (!path.StartsWith(_dohwwwFolder) || !File.Exists(path))
                            {
                                await SendErrorAsync(stream, 404);
                                break;
                            }

                            await SendFileAsync(stream, path);
                            break;
                    }
                }
            }
            catch (TimeoutException)
            {
                //ignore timeout exception
            }
            catch (IOException)
            {
                //ignore IO exceptions
            }
            catch (Exception ex)
            {
                LogManager queryLog = _queryLog;
                if ((queryLog != null) && (dnsRequest != null))
                    queryLog.Write(remoteEP, dnsProtocol, dnsRequest, null);

                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP, dnsProtocol, ex);

                await SendErrorAsync(stream, ex);
            }
        }

        private static async Task SendContentAsync(Stream outputStream, string contentType, byte[] bufferContent)
        {
            byte[] bufferHeader = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nDate: " + DateTime.UtcNow.ToString("r") + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + bufferContent.Length + "\r\nX-Robots-Tag: noindex, nofollow\r\n\r\n");

            await outputStream.WriteAsync(bufferHeader, 0, bufferHeader.Length);
            await outputStream.WriteAsync(bufferContent, 0, bufferContent.Length);
            await outputStream.FlushAsync();
        }

        private static Task SendErrorAsync(Stream outputStream, Exception ex)
        {
            return SendErrorAsync(outputStream, 500, ex.ToString());
        }

        private static async Task SendErrorAsync(Stream outputStream, int statusCode, string message = null)
        {
            try
            {
                string statusString = statusCode + " " + GetHttpStatusString((HttpStatusCode)statusCode);
                byte[] bufferContent = Encoding.UTF8.GetBytes("<html><head><title>" + statusString + "</title></head><body><h1>" + statusString + "</h1>" + (message == null ? "" : "<p>" + message + "</p>") + "</body></html>");
                byte[] bufferHeader = Encoding.UTF8.GetBytes("HTTP/1.1 " + statusString + "\r\nDate: " + DateTime.UtcNow.ToString("r") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bufferContent.Length + "\r\nX-Robots-Tag: noindex, nofollow\r\n\r\n");

                await outputStream.WriteAsync(bufferHeader, 0, bufferHeader.Length);
                await outputStream.WriteAsync(bufferContent, 0, bufferContent.Length);
                await outputStream.FlushAsync();
            }
            catch
            { }
        }

        private static async Task SendFileAsync(Stream outputStream, string filePath)
        {
            using (FileStream fS = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] bufferHeader = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nDate: " + DateTime.UtcNow.ToString("r") + "\r\nContent-Type: " + WebUtilities.GetContentType(filePath).MediaType + "\r\nContent-Length: " + fS.Length + "\r\nCache-Control: private, max-age=300\r\nX-Robots-Tag: noindex, nofollow\r\n\r\n");

                await outputStream.WriteAsync(bufferHeader, 0, bufferHeader.Length);
                await fS.CopyToAsync(outputStream);
                await outputStream.FlushAsync();
            }
        }

        internal static string GetHttpStatusString(HttpStatusCode statusCode)
        {
            StringBuilder sb = new StringBuilder();

            foreach (char c in statusCode.ToString().ToCharArray())
            {
                if (char.IsUpper(c) && sb.Length > 0)
                    sb.Append(' ');

                sb.Append(c);
            }

            return sb.ToString();
        }

        private bool IsRecursionAllowed(IPEndPoint remoteEP)
        {
            if (!_allowRecursion)
                return false;

            if (_allowRecursionOnlyForPrivateNetworks)
            {
                switch (remoteEP.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                    case AddressFamily.InterNetworkV6:
                        return NetUtilities.IsPrivateIP(remoteEP.Address);

                    default:
                        return false;
                }
            }

            return true;
        }

        private async Task<DnsDatagram> ProcessQueryAsync(DnsDatagram request, IPEndPoint remoteEP, bool isRecursionAllowed, DnsTransportProtocol protocol)
        {
            if (request.IsResponse)
                return null;

            switch (request.OPCODE)
            {
                case DnsOpcode.StandardQuery:
                    if ((request.Question.Count != 1) || (request.Question[0].Class != DnsClass.IN))
                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, isRecursionAllowed, false, false, DnsResponseCode.Refused, request.Question);

                    try
                    {
                        switch (request.Question[0].Type)
                        {
                            case DnsResourceRecordType.AXFR:
                                if (protocol == DnsTransportProtocol.Udp)
                                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.FormatError, request.Question);

                                return await ProcessZoneTransferQueryAsync(request, remoteEP);

                            case DnsResourceRecordType.IXFR:
                                return await ProcessZoneTransferQueryAsync(request, remoteEP);

                            case DnsResourceRecordType.MAILB:
                            case DnsResourceRecordType.MAILA:
                                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, isRecursionAllowed, false, false, DnsResponseCode.NotImplemented, request.Question);

                            default:
                                DnsDatagram response;

                                //check in allowed zone
                                bool inAllowedZone = _allowedZoneManager.Query(request).RCODE != DnsResponseCode.Refused;
                                if (!inAllowedZone)
                                {
                                    //check in blocked zone and block list zone
                                    response = ProcessBlockedQuery(request);
                                    if (response != null)
                                        return response;
                                }

                                //query authoritative zone
                                response = await ProcessAuthoritativeQueryAsync(request, inAllowedZone, isRecursionAllowed);

                                if ((response.RCODE != DnsResponseCode.Refused) || !request.RecursionDesired || !isRecursionAllowed)
                                    return response;

                                //do recursive query
                                return await ProcessRecursiveQueryAsync(request, null, null, !inAllowedZone, false);
                        }
                    }
                    catch (InvalidDomainNameException)
                    {
                        //format error response
                        return new DnsDatagram(request.Identifier, true, request.OPCODE, false, false, request.RecursionDesired, IsRecursionAllowed(remoteEP), false, false, DnsResponseCode.FormatError, request.Question);
                    }
                    catch (Exception ex)
                    {
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP, protocol, ex);

                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, isRecursionAllowed, false, false, DnsResponseCode.ServerFailure, request.Question);
                    }

                case DnsOpcode.Notify:
                    return await ProcessNotifyQueryAsync(request, remoteEP);

                default:
                    return new DnsDatagram(request.Identifier, true, request.OPCODE, false, false, request.RecursionDesired, isRecursionAllowed, false, false, DnsResponseCode.NotImplemented, request.Question);
            }
        }

        private async Task<DnsDatagram> ProcessNotifyQueryAsync(DnsDatagram request, IPEndPoint remoteEP)
        {
            AuthZoneInfo authZoneInfo = _authZoneManager.GetAuthZoneInfo(request.Question[0].Name);
            if ((authZoneInfo == null) || (authZoneInfo.Type != AuthZoneType.Secondary))
                return new DnsDatagram(request.Identifier, true, DnsOpcode.Notify, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.Refused, request.Question) { Tag = StatsResponseType.Authoritative };

            IPAddress remoteAddress = remoteEP.Address;
            bool remoteVerified = false;

            IReadOnlyList<NameServerAddress> primaryNameServers = await authZoneInfo.GetPrimaryNameServerAddressesAsync(this);

            foreach (NameServerAddress primaryNameServer in primaryNameServers)
            {
                if (primaryNameServer.IPEndPoint.Address.Equals(remoteAddress))
                {
                    remoteVerified = true;
                    break;
                }
            }

            if (!remoteVerified)
                return new DnsDatagram(request.Identifier, true, DnsOpcode.Notify, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.Refused, request.Question) { Tag = StatsResponseType.Authoritative };

            LogManager log = _log;
            if (log != null)
                log.Write(remoteEP, "DNS Server received NOTIFY for zone: " + authZoneInfo.Name);

            if ((request.Answer.Count > 0) && (request.Answer[0].Type == DnsResourceRecordType.SOA))
            {
                IReadOnlyList<DnsResourceRecord> localSoaRecords = authZoneInfo.GetRecords(DnsResourceRecordType.SOA);

                if (!DnsSOARecord.IsZoneUpdateAvailable((localSoaRecords[0].RDATA as DnsSOARecord).Serial, (request.Answer[0].RDATA as DnsSOARecord).Serial))
                {
                    //no update was available
                    return new DnsDatagram(request.Identifier, true, DnsOpcode.Notify, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, request.Question) { Tag = StatsResponseType.Authoritative };
                }
            }

            authZoneInfo.RefreshZone();
            return new DnsDatagram(request.Identifier, true, DnsOpcode.Notify, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, request.Question) { Tag = StatsResponseType.Authoritative };
        }

        private async Task<DnsDatagram> ProcessZoneTransferQueryAsync(DnsDatagram request, IPEndPoint remoteEP)
        {
            AuthZoneInfo authZoneInfo = _authZoneManager.GetAuthZoneInfo(request.Question[0].Name);
            if ((authZoneInfo == null) || (authZoneInfo.Type != AuthZoneType.Primary))
                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.Refused, request.Question) { Tag = StatsResponseType.Authoritative };

            IPAddress remoteAddress = remoteEP.Address;
            bool isAxfrAllowed = IPAddress.IsLoopback(remoteAddress);

            if (!isAxfrAllowed)
            {
                IReadOnlyList<NameServerAddress> secondaryNameServers = await authZoneInfo.GetSecondaryNameServerAddressesAsync(this);

                foreach (NameServerAddress secondaryNameServer in secondaryNameServers)
                {
                    if (secondaryNameServer.IPEndPoint.Address.Equals(remoteAddress))
                    {
                        isAxfrAllowed = true;
                        break;
                    }
                }
            }

            if (!isAxfrAllowed)
                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.Refused, request.Question) { Tag = StatsResponseType.Authoritative };

            LogManager log = _log;
            if (log != null)
                log.Write(remoteEP, "DNS Server received zone transfer request for zone: " + authZoneInfo.Name);

            IReadOnlyList<DnsResourceRecord> axfrRecords = _authZoneManager.QueryZoneTransferRecords(request.Question[0].Name);

            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, true, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, request.Question, axfrRecords) { Tag = StatsResponseType.Authoritative };
        }

        private Task<DnsDatagram> ProcessAuthoritativeQueryAsync(DnsDatagram request, bool inAllowedZone, bool isRecursionAllowed)
        {
            DnsDatagram response = _authZoneManager.Query(request);
            response.Tag = StatsResponseType.Authoritative;

            if (response.RCODE == DnsResponseCode.NoError)
            {
                if (response.Answer.Count > 0)
                {
                    DnsResourceRecordType questionType = request.Question[0].Type;
                    DnsResourceRecord lastRR = response.Answer[response.Answer.Count - 1];

                    if ((lastRR.Type != questionType) && (questionType != DnsResourceRecordType.ANY))
                    {
                        switch (lastRR.Type)
                        {
                            case DnsResourceRecordType.CNAME:
                                return ProcessCNAMEAsync(request, response, isRecursionAllowed, false);

                            case DnsResourceRecordType.ANAME:
                                return ProcessANAMEAsync(request, response, isRecursionAllowed);
                        }
                    }
                }
                else if (response.Authority.Count > 0)
                {
                    switch (response.Authority[0].Type)
                    {
                        case DnsResourceRecordType.NS:
                            if (request.RecursionDesired && isRecursionAllowed)
                            {
                                //do recursive resolution using response authority name servers
                                List<NameServerAddress> nameServers = NameServerAddress.GetNameServersFromResponse(response, _preferIPv6, false);

                                return ProcessRecursiveQueryAsync(request, nameServers, null, !inAllowedZone, false);
                            }

                            break;

                        case DnsResourceRecordType.FWD:
                            if ((response.Authority.Count == 1) && (response.Authority[0].Type == DnsResourceRecordType.FWD) && (response.Authority[0].RDATA as DnsForwarderRecord).Forwarder.Equals("this-server", StringComparison.OrdinalIgnoreCase))
                            {
                                //do conditional forwarding via "this-server" 
                                return ProcessRecursiveQueryAsync(request, null, null, !inAllowedZone, false);
                            }
                            else
                            {
                                //do conditional forwarding
                                List<NameServerAddress> forwarders = new List<NameServerAddress>(response.Authority.Count);

                                foreach (DnsResourceRecord rr in response.Authority)
                                {
                                    if (rr.Type == DnsResourceRecordType.FWD)
                                    {
                                        DnsForwarderRecord fwd = rr.RDATA as DnsForwarderRecord;

                                        if (!fwd.Forwarder.Equals("this-server", StringComparison.OrdinalIgnoreCase))
                                            forwarders.Add(fwd.NameServer);
                                    }
                                }

                                return ProcessRecursiveQueryAsync(request, null, forwarders, !inAllowedZone, false);
                            }
                    }
                }
            }

            if (response.RecursionAvailable != isRecursionAllowed)
                response = new DnsDatagram(response.Identifier, response.IsResponse, response.OPCODE, response.AuthoritativeAnswer, response.Truncation, response.RecursionDesired, isRecursionAllowed, response.AuthenticData, response.CheckingDisabled, response.RCODE, response.Question, response.Answer, response.Authority, response.Additional);

            return Task.FromResult(response);
        }

        private async Task<DnsDatagram> ProcessCNAMEAsync(DnsDatagram request, DnsDatagram response, bool isRecursionAllowed, bool cacheRefreshOperation)
        {
            List<DnsResourceRecord> responseAnswer = new List<DnsResourceRecord>();
            responseAnswer.AddRange(response.Answer);

            DnsDatagram lastResponse;
            bool isAuthoritativeAnswer = response.AuthoritativeAnswer;
            string lastDomain = (response.Answer[response.Answer.Count - 1].RDATA as DnsCNAMERecord).Domain;

            int queryCount = 0;
            do
            {
                DnsDatagram newRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(lastDomain, request.Question[0].Type, request.Question[0].Class) });

                //query authoritative zone first
                lastResponse = _authZoneManager.Query(newRequest);

                if (lastResponse.RCODE == DnsResponseCode.Refused)
                {
                    //not found in auth zone
                    if (newRequest.RecursionDesired && isRecursionAllowed)
                    {
                        //do recursion
                        lastResponse = await RecursiveResolveAsync(newRequest, null, null, false, cacheRefreshOperation);
                        isAuthoritativeAnswer = false;
                    }
                    else
                    {
                        //break since no recursion allowed/desired
                        break;
                    }
                }
                else if ((lastResponse.Answer.Count > 0) && (lastResponse.Answer[0].Type == DnsResourceRecordType.ANAME))
                {
                    lastResponse = await ProcessANAMEAsync(request, lastResponse, isRecursionAllowed);
                }
                else if ((lastResponse.Answer.Count == 0) && (lastResponse.Authority.Count > 0))
                {
                    //found delegated/forwarded zone
                    switch (lastResponse.Authority[0].Type)
                    {
                        case DnsResourceRecordType.NS:
                            if (newRequest.RecursionDesired && isRecursionAllowed)
                            {
                                //do recursive resolution using last response authority name servers
                                List<NameServerAddress> nameServers = NameServerAddress.GetNameServersFromResponse(lastResponse, _preferIPv6, false);

                                lastResponse = await RecursiveResolveAsync(newRequest, nameServers, null, false, false);
                                isAuthoritativeAnswer = false;
                            }

                            break;

                        case DnsResourceRecordType.FWD:
                            if ((lastResponse.Authority.Count == 1) && (lastResponse.Authority[0].RDATA as DnsForwarderRecord).Forwarder.Equals("this-server", StringComparison.OrdinalIgnoreCase))
                            {
                                //do conditional forwarding via "this-server" 
                                lastResponse = await RecursiveResolveAsync(newRequest, null, null, false, false);
                                isAuthoritativeAnswer = false;
                            }
                            else
                            {
                                //do conditional forwarding
                                List<NameServerAddress> forwarders = new List<NameServerAddress>(lastResponse.Authority.Count);

                                foreach (DnsResourceRecord rr in lastResponse.Authority)
                                {
                                    if (rr.Type == DnsResourceRecordType.FWD)
                                    {
                                        DnsForwarderRecord fwd = rr.RDATA as DnsForwarderRecord;

                                        if (!fwd.Forwarder.Equals("this-server", StringComparison.OrdinalIgnoreCase))
                                            forwarders.Add(fwd.NameServer);
                                    }
                                }

                                lastResponse = await RecursiveResolveAsync(newRequest, null, forwarders, false, false);
                                isAuthoritativeAnswer = false;
                            }

                            break;
                    }
                }

                //check last response
                if (lastResponse.Answer.Count == 0)
                    break; //cannot proceed to resolve further

                responseAnswer.AddRange(lastResponse.Answer);

                DnsResourceRecord lastRR = lastResponse.Answer[lastResponse.Answer.Count - 1];

                if (lastRR.Type != DnsResourceRecordType.CNAME)
                    break; //cname was resolved

                lastDomain = (lastRR.RDATA as DnsCNAMERecord).Domain;
            }
            while (++queryCount < MAX_CNAME_HOPS);

            DnsResponseCode rcode;
            IReadOnlyList<DnsResourceRecord> authority = null;
            IReadOnlyList<DnsResourceRecord> additional = null;

            if ((lastResponse.RCODE == DnsResponseCode.Refused) && !(request.RecursionDesired && isRecursionAllowed))
            {
                rcode = DnsResponseCode.NoError;
            }
            else
            {
                rcode = lastResponse.RCODE;

                if (isAuthoritativeAnswer)
                {
                    authority = response.Authority;
                    additional = response.Additional;
                }
                else
                {
                    if ((lastResponse.Authority.Count > 0) && (lastResponse.Authority[0].Type == DnsResourceRecordType.SOA))
                    {
                        authority = lastResponse.Authority;
                    }
                    else
                    {
                        switch (request.Question[0].Type)
                        {
                            case DnsResourceRecordType.NS:
                            case DnsResourceRecordType.MX:
                            case DnsResourceRecordType.SRV:
                                additional = lastResponse.Additional;
                                break;
                        }
                    }
                }
            }

            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, isAuthoritativeAnswer, false, request.RecursionDesired, isRecursionAllowed, false, false, rcode, request.Question, responseAnswer, authority, additional) { Tag = response.Tag };
        }

        private async Task<DnsDatagram> ProcessANAMEAsync(DnsDatagram request, DnsDatagram response, bool isRecursionAllowed)
        {
            List<DnsResourceRecord> responseAnswer = new List<DnsResourceRecord>();

            for (int i = 0; i < response.Answer.Count - 1; i++)
                responseAnswer.Add(response.Answer[i]);

            DnsDatagram lastResponse;
            DnsResourceRecord anameRR = response.Answer[response.Answer.Count - 1];
            string lastDomain = (anameRR.RDATA as DnsANAMERecord).Domain;

            int queryCount = 0;
            do
            {
                DnsDatagram newRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(lastDomain, request.Question[0].Type, request.Question[0].Class) });

                //query authoritative zone first
                lastResponse = _authZoneManager.Query(newRequest);

                if (lastResponse.RCODE == DnsResponseCode.Refused)
                {
                    //not found in auth zone; do recursion
                    lastResponse = await RecursiveResolveAsync(newRequest, null, null, false, false);
                }
                else if ((lastResponse.Answer.Count == 0) && (lastResponse.Authority.Count > 0))
                {
                    //found delegated/forwarded zone
                    switch (lastResponse.Authority[0].Type)
                    {
                        case DnsResourceRecordType.NS:
                            //do recursive resolution using last response authority name servers
                            List<NameServerAddress> nameServers = NameServerAddress.GetNameServersFromResponse(lastResponse, _preferIPv6, false);

                            lastResponse = await RecursiveResolveAsync(newRequest, nameServers, null, false, false);
                            break;

                        case DnsResourceRecordType.FWD:
                            if ((lastResponse.Authority.Count == 1) && (lastResponse.Authority[0].RDATA as DnsForwarderRecord).Forwarder.Equals("this-server", StringComparison.OrdinalIgnoreCase))
                            {
                                //do conditional forwarding via "this-server" 
                                lastResponse = await RecursiveResolveAsync(newRequest, null, null, false, false);
                            }
                            else
                            {
                                //do conditional forwarding
                                List<NameServerAddress> forwarders = new List<NameServerAddress>(lastResponse.Authority.Count);

                                foreach (DnsResourceRecord rr in lastResponse.Authority)
                                {
                                    if (rr.Type == DnsResourceRecordType.FWD)
                                    {
                                        DnsForwarderRecord fwd = rr.RDATA as DnsForwarderRecord;

                                        if (!fwd.Forwarder.Equals("this-server", StringComparison.OrdinalIgnoreCase))
                                            forwarders.Add(fwd.NameServer);
                                    }
                                }

                                lastResponse = await RecursiveResolveAsync(newRequest, null, forwarders, false, false);
                            }

                            break;
                    }
                }

                //check last response
                if (lastResponse.Answer.Count == 0)
                    break; //cannot proceed to resolve further

                DnsResourceRecord firstRR = lastResponse.Answer[0];
                if (firstRR.Type == request.Question[0].Type)
                {
                    foreach (DnsResourceRecord answer in lastResponse.Answer)
                    {
                        if (anameRR.TtlValue < answer.TtlValue)
                            responseAnswer.Add(new DnsResourceRecord(anameRR.Name, answer.Type, answer.Class, anameRR.TtlValue, answer.RDATA));
                        else
                            responseAnswer.Add(new DnsResourceRecord(anameRR.Name, answer.Type, answer.Class, answer.TtlValue, answer.RDATA));
                    }

                    break; //found final answer
                }

                DnsResourceRecord lastRR = lastResponse.Answer[lastResponse.Answer.Count - 1];

                if (lastRR.Type == DnsResourceRecordType.ANAME)
                    lastDomain = (lastRR.RDATA as DnsANAMERecord).Domain;
                else if (lastRR.Type == DnsResourceRecordType.CNAME)
                    lastDomain = (lastRR.RDATA as DnsCNAMERecord).Domain;
                else
                    break; //aname/cname was resolved
            }
            while (++queryCount < MAX_CNAME_HOPS);

            DnsResponseCode rcode = lastResponse.RCODE;
            if (rcode == DnsResponseCode.NameError)
                rcode = DnsResponseCode.NoError;

            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, true, false, request.RecursionDesired, isRecursionAllowed, false, false, rcode, request.Question, responseAnswer, response.Authority, response.Additional) { Tag = response.Tag };
        }

        private DnsDatagram ProcessBlockedQuery(DnsDatagram request)
        {
            DnsDatagram response = _blockedZoneManager.Query(request);
            if (response.RCODE == DnsResponseCode.Refused)
            {
                //domain not blocked in blocked zone
                response = _blockListZoneManager.Query(request); //check in block list zone
                if (response == null)
                    return null;
            }
            else
            {
                //domain is blocked in blocked zone
                IReadOnlyList<DnsResourceRecord> answer = null;
                IReadOnlyList<DnsResourceRecord> authority = null;

                switch (response.Question[0].Type)
                {
                    case DnsResourceRecordType.A:
                        answer = new DnsResourceRecord[] { new DnsResourceRecord(response.Question[0].Name, DnsResourceRecordType.A, response.Question[0].Class, 60, _aRecord) };
                        break;

                    case DnsResourceRecordType.AAAA:
                        answer = new DnsResourceRecord[] { new DnsResourceRecord(response.Question[0].Name, DnsResourceRecordType.AAAA, response.Question[0].Class, 60, _aaaaRecord) };
                        break;

                    case DnsResourceRecordType.NS:
                        answer = response.Answer;
                        break;

                    case DnsResourceRecordType.TXT:
                        answer = new DnsResourceRecord[] { new DnsResourceRecord(response.Question[0].Name, DnsResourceRecordType.TXT, response.Question[0].Class, 60, new DnsTXTRecord("blockList=custom; domain=" + response.Question[0].Name)) };
                        break;

                    default:
                        authority = response.Authority;
                        break;
                }

                response = new DnsDatagram(response.Identifier, true, response.OPCODE, false, false, response.RecursionDesired, true, false, false, DnsResponseCode.NoError, response.Question, answer, authority);
            }

            response.Tag = StatsResponseType.Blocked;
            return response;
        }

        private async Task<DnsDatagram> ProcessRecursiveQueryAsync(DnsDatagram request, IReadOnlyList<NameServerAddress> viaNameServers, IReadOnlyList<NameServerAddress> viaForwarders, bool checkForCnameCloaking, bool cacheRefreshOperation)
        {
            DnsDatagram response = await RecursiveResolveAsync(request, viaNameServers, viaForwarders, false, cacheRefreshOperation);

            if (response.Answer.Count > 0)
            {
                DnsResourceRecordType questionType = request.Question[0].Type;
                DnsResourceRecord lastRR = response.Answer[response.Answer.Count - 1];

                if ((lastRR.Type != questionType) && (lastRR.Type == DnsResourceRecordType.CNAME) && (questionType != DnsResourceRecordType.ANY))
                    response = await ProcessCNAMEAsync(request, response, true, cacheRefreshOperation);

                if (checkForCnameCloaking)
                {
                    for (int i = 0; i < response.Answer.Count; i++)
                    {
                        DnsResourceRecord record = response.Answer[i];

                        if (record.Type != DnsResourceRecordType.CNAME)
                            break; //no further CNAME records exists

                        DnsDatagram newRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord((record.RDATA as DnsCNAMERecord).Domain, request.Question[0].Type, request.Question[0].Class) });

                        //check allowed zone
                        bool inAllowedZone = _allowedZoneManager.Query(newRequest).RCODE != DnsResponseCode.Refused;
                        if (inAllowedZone)
                            break; //CNAME is in allowed zone

                        //check blocked zone and block list zone
                        DnsDatagram lastResponse = ProcessBlockedQuery(newRequest);
                        if (lastResponse != null)
                        {
                            //found cname cloaking
                            List<DnsResourceRecord> answer = new List<DnsResourceRecord>();

                            //copy current and previous CNAME records
                            for (int j = 0; j <= i; j++)
                                answer.Add(response.Answer[j]);

                            //copy last response answers
                            answer.AddRange(lastResponse.Answer);

                            IReadOnlyList<DnsResourceRecord> authority = null;
                            IReadOnlyList<DnsResourceRecord> additional = null;

                            if ((lastResponse.Authority.Count > 0) && (lastResponse.Authority[0].Type == DnsResourceRecordType.SOA))
                            {
                                authority = lastResponse.Authority;
                            }
                            else
                            {
                                switch (questionType)
                                {
                                    case DnsResourceRecordType.NS:
                                    case DnsResourceRecordType.MX:
                                    case DnsResourceRecordType.SRV:
                                        additional = lastResponse.Additional;
                                        break;
                                }
                            }

                            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, DnsResponseCode.NoError, request.Question, answer, authority, additional) { Tag = lastResponse.Tag };
                        }
                    }
                }
            }

            //return response
            {
                IReadOnlyList<DnsResourceRecord> authority = null;
                IReadOnlyList<DnsResourceRecord> additional = null;

                if ((response.Authority.Count > 0) && (response.Authority[0].Type == DnsResourceRecordType.SOA))
                {
                    authority = response.Authority;
                }
                else
                {
                    switch (request.Question[0].Type)
                    {
                        case DnsResourceRecordType.NS:
                        case DnsResourceRecordType.MX:
                        case DnsResourceRecordType.SRV:
                            additional = response.Additional;
                            break;
                    }
                }

                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, response.RCODE, request.Question, response.Answer, authority, additional) { Tag = response.Tag };
            }
        }

        private async Task<DnsDatagram> RecursiveResolveAsync(DnsDatagram request, IReadOnlyList<NameServerAddress> viaNameServers, IReadOnlyList<NameServerAddress> viaForwarders, bool cachePrefetchOperation, bool cacheRefreshOperation)
        {
            if (!cachePrefetchOperation && !cacheRefreshOperation)
            {
                //query cache zone to see if answer available
                DnsDatagram cacheResponse = QueryCache(request, false);
                if (cacheResponse != null)
                {
                    if (_cachePrefetchTrigger > 0)
                    {
                        //inspect response TTL values to decide if prefetch trigger is needed
                        foreach (DnsResourceRecord answer in cacheResponse.Answer)
                        {
                            if ((answer.OriginalTtlValue > _cachePrefetchEligibility) && (answer.TtlValue < _cachePrefetchTrigger))
                            {
                                //trigger prefetch async
                                _ = PrefetchCacheAsync(request, viaNameServers, viaForwarders);
                                break;
                            }
                        }
                    }

                    return cacheResponse;
                }
            }

            //recursion with locking
            TaskCompletionSource<DnsDatagram> resolverTaskCompletionSource = new TaskCompletionSource<DnsDatagram>();
            Task<DnsDatagram> resolverTask = _resolverTasks.GetOrAdd(GetResolverQueryKey(request.Question[0]), resolverTaskCompletionSource.Task);

            if (resolverTask.Equals(resolverTaskCompletionSource.Task))
            {
                //got new resolver task added so question is not being resolved; do recursive resolution in another task on resolver thread pool
                _ = Task.Factory.StartNew(delegate ()
                {
                    return RecursiveResolveAsync(request, viaNameServers, viaForwarders, cachePrefetchOperation, cacheRefreshOperation, resolverTaskCompletionSource);
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _resolverTaskScheduler);
            }

            //request is being recursively resolved by another thread

            if (cachePrefetchOperation)
                return null; //return null as prefetch worker thread does not need valid response and thus does not need to wait

            DateTime resolverWaitStartTime = DateTime.UtcNow;

            //wait till short timeout for response
            if (await Task.WhenAny(resolverTask, Task.Delay(SERVE_STALE_WAIT_TIME)) == resolverTask) //1.8 sec wait as per draft-ietf-dnsop-serve-stale-04
            {
                //resolver signaled
                DnsDatagram response = await resolverTask;

                if (response != null)
                    return response;

                //resolver had exception and no stale record was found
            }
            else
            {
                //wait timed out

                if (_serveStale)
                {
                    //query cache zone to return stale answer (if available) as per draft-ietf-dnsop-serve-stale-04
                    DnsDatagram staleResponse = QueryCache(request, true);
                    if (staleResponse != null)
                        return staleResponse;
                }

                //wait till full timeout before responding as ServerFailure
                int timeout = Convert.ToInt32(_clientTimeout - (DateTime.UtcNow - resolverWaitStartTime).TotalMilliseconds);
                if (timeout > 0)
                {
                    if (await Task.WhenAny(resolverTask, Task.Delay(timeout)) == resolverTask)
                    {
                        //resolver signaled
                        DnsDatagram response = await resolverTask;

                        if (response != null)
                            return response;
                    }

                    //no response available from resolver or resolver had exception and no stale record was found
                }
            }

            //no response available; respond with ServerFailure
            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.ServerFailure, request.Question);
        }

        private async Task RecursiveResolveAsync(DnsDatagram request, IReadOnlyList<NameServerAddress> viaNameServers, IReadOnlyList<NameServerAddress> viaForwarders, bool cachePrefetchOperation, bool cacheRefreshOperation, TaskCompletionSource<DnsDatagram> taskCompletionSource)
        {
            IReadOnlyList<NameServerAddress> forwarders = _forwarders;
            if (viaForwarders != null)
                forwarders = viaForwarders; //use provided forwarders

            try
            {
                if ((viaNameServers == null) && (forwarders != null))
                {
                    //use forwarders
                    if (_proxy == null)
                    {
                        //recursive resolve name server when proxy is null else let proxy resolve it
                        foreach (NameServerAddress nameServerAddress in forwarders)
                        {
                            if (nameServerAddress.IsIPEndPointStale) //refresh forwarder IPEndPoint if stale
                                await nameServerAddress.RecursiveResolveIPAddressAsync(_dnsCache, null, _preferIPv6, _randomizeName, _resolverRetries, _resolverTimeout);
                        }
                    }

                    //query forwarders and update cache
                    DnsClient dnsClient = new DnsClient(forwarders);

                    dnsClient.Proxy = _proxy;
                    dnsClient.PreferIPv6 = _preferIPv6;
                    dnsClient.RandomizeName = _randomizeName;
                    dnsClient.Retries = _forwarderRetries;
                    dnsClient.Timeout = _forwarderTimeout;
                    dnsClient.Concurrency = _forwarderConcurrency;

                    DnsDatagram response = await dnsClient.ResolveAsync(request.Question[0]);

                    _cacheZoneManager.CacheResponse(response);

                    taskCompletionSource.SetResult(response);
                }
                else
                {
                    //recursive resolve and update cache
                    IDnsCache dnsCache;

                    if (cachePrefetchOperation || cacheRefreshOperation)
                        dnsCache = new ResolverPrefetchDnsCache(_authZoneManager, _cacheZoneManager, request.Question[0]);
                    else
                        dnsCache = _dnsCache;

                    DnsDatagram response = await DnsClient.RecursiveResolveAsync(request.Question[0], viaNameServers, dnsCache, _proxy, _preferIPv6, _randomizeName, _resolverRetries, _resolverTimeout, _resolverMaxStackCount);
                    taskCompletionSource.SetResult(response);
                }
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                {
                    string nameServers = null;

                    if (viaNameServers != null)
                    {
                        foreach (NameServerAddress nameServer in viaNameServers)
                        {
                            if (nameServers == null)
                                nameServers = nameServer.ToString();
                            else
                                nameServers += ", " + nameServer.ToString();
                        }
                    }
                    else if (forwarders != null)
                    {
                        foreach (NameServerAddress nameServer in forwarders)
                        {
                            if (nameServers == null)
                                nameServers = nameServer.ToString();
                            else
                                nameServers += ", " + nameServer.ToString();
                        }
                    }

                    log.Write("DNS Server recursive resolution failed for QNAME: " + request.Question[0].Name + "; QTYPE: " + request.Question[0].Type.ToString() + "; QCLASS: " + request.Question[0].Class.ToString() + (nameServers == null ? "" : "; Name Servers: " + nameServers) + ";\r\n" + ex.ToString());
                }

                if (_serveStale)
                {
                    //fetch stale record
                    DnsDatagram staleResponse = QueryCache(request, true);
                    if (staleResponse == null)
                    {
                        //no stale record was found; signal null response to release waiting tasks
                        taskCompletionSource.SetResult(null);
                    }
                    else
                    {
                        //reset expiry for stale records
                        foreach (DnsResourceRecord record in staleResponse.Answer)
                        {
                            if (record.IsStale)
                                record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                        }

                        //signal stale response
                        taskCompletionSource.SetResult(staleResponse);
                    }
                }
                else
                {
                    //signal null response to release waiting tasks
                    taskCompletionSource.SetResult(null);
                }
            }
            finally
            {
                _resolverTasks.TryRemove(GetResolverQueryKey(request.Question[0]), out _);
            }
        }

        private static string GetResolverQueryKey(DnsQuestionRecord question)
        {
            if (string.IsNullOrEmpty(question.Name))
                return question.Type + "." + question.Class;

            return question.Name + "." + question.Type + "." + question.Class;
        }

        private DnsDatagram QueryCache(DnsDatagram request, bool serveStale)
        {
            DnsDatagram cacheResponse = _cacheZoneManager.Query(request, serveStale);

            if (cacheResponse.RCODE != DnsResponseCode.Refused)
            {
                if ((cacheResponse.Answer.Count > 0) || (cacheResponse.Authority.Count == 0) || (cacheResponse.Authority[0].Type == DnsResourceRecordType.SOA))
                {
                    cacheResponse.Tag = StatsResponseType.Cached;

                    return cacheResponse;
                }
            }

            return null;
        }

        private async Task PrefetchCacheAsync(DnsDatagram request, IReadOnlyList<NameServerAddress> viaNameServers, IReadOnlyList<NameServerAddress> viaForwarders)
        {
            try
            {
                await RecursiveResolveAsync(request, viaNameServers, viaForwarders, true, false);
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(ex);
            }
        }

        private async Task RefreshCacheAsync(IList<CacheRefreshSample> cacheRefreshSampleList, CacheRefreshSample sample, int sampleQuestionIndex)
        {
            try
            {
                //refresh cache
                DnsDatagram request = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { sample.SampleQuestion });
                DnsDatagram response = await ProcessRecursiveQueryAsync(request, sample.ViaNameServers, sample.ViaForwarders, false, true);

                bool removeFromSampleList = true;
                DateTime utcNow = DateTime.UtcNow;

                foreach (DnsResourceRecord answer in response.Answer)
                {
                    if ((answer.OriginalTtlValue > _cachePrefetchEligibility) && (utcNow.AddSeconds(answer.TtlValue) < _cachePrefetchSamplingTimerTriggersOn))
                    {
                        //answer expires before next sampling so dont remove from list to allow refreshing it
                        removeFromSampleList = false;
                        break;
                    }
                }

                if (removeFromSampleList)
                    cacheRefreshSampleList[sampleQuestionIndex] = null;
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(ex);
            }
        }

        private DnsQuestionRecord GetCacheRefreshNeededQuery(DnsQuestionRecord question, int trigger)
        {
            int queryCount = 0;

            while (true)
            {
                DnsDatagram cacheResponse = QueryCache(new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { question }), false);
                if (cacheResponse == null)
                    return question; //cache expired so refresh question

                if (cacheResponse.Answer.Count == 0)
                    return null; //dont refresh empty responses

                //inspect response TTL values to decide if refresh is needed
                foreach (DnsResourceRecord answer in cacheResponse.Answer)
                {
                    if ((answer.OriginalTtlValue > _cachePrefetchEligibility) && (answer.TtlValue < trigger))
                        return question; //TTL eligible and less than trigger so refresh question
                }

                DnsResourceRecord lastRR = cacheResponse.Answer[cacheResponse.Answer.Count - 1];

                if (lastRR.Type == question.Type)
                    return null; //answer was resolved

                if (lastRR.Type != DnsResourceRecordType.CNAME)
                    return null; //invalid response so ignore question

                queryCount++;
                if (queryCount >= MAX_CNAME_HOPS)
                    return null; //too many hops so ignore question

                //follow CNAME chain to inspect TTL further
                question = new DnsQuestionRecord((lastRR.RDATA as DnsCNAMERecord).Domain, question.Type, question.Class);
            }
        }

        private bool IsCacheRefreshNeeded(DnsQuestionRecord question, int trigger)
        {
            DnsDatagram cacheResponse = QueryCache(new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { question }), false);
            if (cacheResponse == null)
                return true; //cache expired so refresh needed

            if (cacheResponse.Answer.Count == 0)
                return false; //dont refresh empty responses

            //inspect response TTL values to decide if refresh is needed
            foreach (DnsResourceRecord answer in cacheResponse.Answer)
            {
                if ((answer.OriginalTtlValue > _cachePrefetchEligibility) && (answer.TtlValue < trigger))
                    return true; //TTL eligible less than trigger so refresh
            }

            return false; //no need to refresh for this query
        }

        private void CachePrefetchSamplingTimerCallback(object state)
        {
            try
            {
                StatsManager stats = _stats;
                if (stats != null)
                {
                    List<KeyValuePair<DnsQuestionRecord, int>> eligibleQueries = stats.GetLastHourEligibleQueries(_cachePrefetchSampleEligibilityHitsPerHour);
                    List<CacheRefreshSample> cacheRefreshSampleList = new List<CacheRefreshSample>(eligibleQueries.Count);
                    int cacheRefreshTrigger = (_cachePrefetchSampleIntervalInMinutes + 1) * 60;

                    foreach (KeyValuePair<DnsQuestionRecord, int> query in eligibleQueries)
                    {
                        IReadOnlyList<NameServerAddress> viaNameServers = null;
                        IReadOnlyList<NameServerAddress> viaForwarders = null;

                        AuthZoneInfo zoneInfo = _authZoneManager.GetAuthZoneInfo(query.Key.Name);
                        if ((zoneInfo != null) && !zoneInfo.Disabled)
                        {
                            switch (zoneInfo.Type)
                            {
                                case AuthZoneType.Primary:
                                case AuthZoneType.Secondary:
                                    //zone is hosted
                                    continue; //no cache refresh for hosted zones

                                case AuthZoneType.Stub: //stub zone refresh via its name servers
                                    {
                                        IReadOnlyList<DnsResourceRecord> nsRecords = zoneInfo.GetRecords(DnsResourceRecordType.NS);

                                        List<NameServerAddress> nameServers = new List<NameServerAddress>();

                                        foreach (DnsResourceRecord nsRecord in nsRecords)
                                        {
                                            string nsDomain = (nsRecord.RDATA as DnsNSRecord).NameServer;

                                            IReadOnlyList<DnsResourceRecord> glueRecords = nsRecord.GetGlueRecords();
                                            if (glueRecords.Count > 0)
                                            {
                                                foreach (DnsResourceRecord glueRecord in glueRecords)
                                                {
                                                    switch (glueRecord.Type)
                                                    {
                                                        case DnsResourceRecordType.A:
                                                            nameServers.Add(new NameServerAddress(nsDomain, (glueRecord.RDATA as DnsARecord).Address));
                                                            break;

                                                        case DnsResourceRecordType.AAAA:
                                                            if (_preferIPv6)
                                                                nameServers.Add(new NameServerAddress(nsDomain, (glueRecord.RDATA as DnsAAAARecord).Address));

                                                            break;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                nameServers.Add(new NameServerAddress(nsDomain));
                                            }
                                        }

                                        viaNameServers = nameServers;
                                    }
                                    break;

                                case AuthZoneType.Forwarder: //forwarder zone refresh via its forwarders
                                    {
                                        IReadOnlyList<DnsResourceRecord> fwdRecords = zoneInfo.GetRecords(DnsResourceRecordType.FWD);

                                        List<NameServerAddress> forwarders = new List<NameServerAddress>();

                                        foreach (DnsResourceRecord fwdRecord in fwdRecords)
                                        {
                                            if (!fwdRecord.IsDisabled())
                                                forwarders.Add((fwdRecord.RDATA as DnsForwarderRecord).NameServer);
                                        }

                                        viaForwarders = forwarders;
                                    }
                                    break;
                            }
                        }

                        if (query.Key.Type == DnsResourceRecordType.ANY)
                            continue; //dont refresh ANY queries

                        DnsQuestionRecord refreshQuery = GetCacheRefreshNeededQuery(query.Key, cacheRefreshTrigger);
                        if (refreshQuery != null)
                        {
                            if ((viaNameServers != null) && !refreshQuery.Name.Equals(query.Key.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                //stub zone case where refresh query is a CNAME of the original query
                                if (!refreshQuery.Name.Equals(zoneInfo.Name, StringComparison.OrdinalIgnoreCase) && !refreshQuery.Name.EndsWith("." + zoneInfo.Name, StringComparison.OrdinalIgnoreCase))
                                    viaNameServers = null; //refresh query is a CNAME that is outside of the stub zone so do usual recursive resolution
                            }

                            cacheRefreshSampleList.Add(new CacheRefreshSample(refreshQuery, viaNameServers, viaForwarders));
                        }
                    }

                    _cacheRefreshSampleList = cacheRefreshSampleList;
                }
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(ex);
            }
            finally
            {
                lock (_cachePrefetchSamplingTimerLock)
                {
                    if (_cachePrefetchSamplingTimer != null)
                    {
                        _cachePrefetchSamplingTimer.Change(_cachePrefetchSampleIntervalInMinutes * 60 * 1000, Timeout.Infinite);
                        _cachePrefetchSamplingTimerTriggersOn = DateTime.UtcNow.AddMinutes(_cachePrefetchSampleIntervalInMinutes);
                    }
                }
            }
        }

        private void CachePrefetchRefreshTimerCallback(object state)
        {
            try
            {
                IList<CacheRefreshSample> cacheRefreshSampleList = _cacheRefreshSampleList;
                if (cacheRefreshSampleList != null)
                {
                    for (int i = 0; i < cacheRefreshSampleList.Count; i++)
                    {
                        CacheRefreshSample sample = cacheRefreshSampleList[i];
                        if (sample == null)
                            continue;

                        if (!IsCacheRefreshNeeded(sample.SampleQuestion, _cachePrefetchTrigger + 2))
                            continue;

                        _ = RefreshCacheAsync(cacheRefreshSampleList, sample, i);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(ex);
            }
            finally
            {
                lock (_cachePrefetchRefreshTimerLock)
                {
                    if (_cachePrefetchRefreshTimer != null)
                        _cachePrefetchRefreshTimer.Change((_cachePrefetchTrigger + 1) * 1000, Timeout.Infinite);
                }
            }
        }

        private void CacheMaintenanceTimerCallback(object state)
        {
            try
            {
                _cacheZoneManager.DoMaintenance();
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(ex);
            }
        }

        private void ResetPrefetchTimers()
        {
            if (_cachePrefetchTrigger == 0)
            {
                lock (_cachePrefetchSamplingTimerLock)
                {
                    if (_cachePrefetchSamplingTimer != null)
                        _cachePrefetchSamplingTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }

                lock (_cachePrefetchRefreshTimerLock)
                {
                    if (_cachePrefetchRefreshTimer != null)
                        _cachePrefetchRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            else if (_state == ServiceState.Running)
            {
                lock (_cachePrefetchSamplingTimerLock)
                {
                    if (_cachePrefetchSamplingTimer != null)
                    {
                        _cachePrefetchSamplingTimer.Change(_cachePrefetchSampleIntervalInMinutes * 60 * 1000, Timeout.Infinite);
                        _cachePrefetchSamplingTimerTriggersOn = DateTime.UtcNow.AddMinutes(_cachePrefetchSampleIntervalInMinutes);
                    }
                }

                lock (_cachePrefetchRefreshTimerLock)
                {
                    if (_cachePrefetchRefreshTimer != null)
                        _cachePrefetchRefreshTimer.Change(CACHE_PREFETCH_REFRESH_TIMER_INITIAL_INTEVAL, Timeout.Infinite);
                }
            }
        }

        private void UpdateThisServer()
        {
            if (_thisServer == null)
            {
                if ((_localEndPoints == null) || (_localEndPoints.Count == 0))
                    _thisServer = new NameServerAddress(_serverDomain, IPAddress.Loopback);
                else if (_localEndPoints[0].Address.Equals(IPAddress.Any))
                    _thisServer = new NameServerAddress(_serverDomain, new IPEndPoint(IPAddress.Loopback, _localEndPoints[0].Port));
                else if (_localEndPoints[0].Equals(IPAddress.IPv6Any))
                    _thisServer = new NameServerAddress(_serverDomain, new IPEndPoint(IPAddress.IPv6Loopback, _localEndPoints[0].Port));
                else
                    _thisServer = new NameServerAddress(_serverDomain, _localEndPoints[0]);
            }
            else
            {
                _thisServer = new NameServerAddress(_serverDomain, _thisServer.IPEndPoint);
            }
        }

        #endregion

        #region public

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException("DnsServer");

            if (_state != ServiceState.Stopped)
                throw new InvalidOperationException("DNS Server is already running.");

            _state = ServiceState.Starting;

            //bind on all local end points
            foreach (IPEndPoint localEP in _localEndPoints)
            {
                Socket udpListener = null;

                try
                {
                    udpListener = new Socket(localEP.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                    #region this code ignores ICMP port unreachable responses which creates SocketException in ReceiveFrom()

                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        const uint IOC_IN = 0x80000000;
                        const uint IOC_VENDOR = 0x18000000;
                        const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

                        udpListener.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
                    }

                    #endregion

                    udpListener.ReceiveBufferSize = 64 * 1024;
                    udpListener.SendBufferSize = 64 * 1024;

                    udpListener.Bind(localEP);

                    _udpListeners.Add(udpListener);

                    LogManager log = _log;
                    if (log != null)
                        log.Write(localEP, DnsTransportProtocol.Udp, "DNS Server was bound successfully.");
                }
                catch (Exception ex)
                {
                    LogManager log = _log;
                    if (log != null)
                        log.Write(localEP, DnsTransportProtocol.Udp, "DNS Server failed to bind.\r\n" + ex.ToString());

                    if (udpListener != null)
                        udpListener.Dispose();
                }

                Socket tcpListener = null;

                try
                {
                    tcpListener = new Socket(localEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    tcpListener.Bind(localEP);
                    tcpListener.Listen(100);

                    _tcpListeners.Add(tcpListener);

                    LogManager log = _log;
                    if (log != null)
                        log.Write(localEP, DnsTransportProtocol.Tcp, "DNS Server was bound successfully.");
                }
                catch (Exception ex)
                {
                    LogManager log = _log;
                    if (log != null)
                        log.Write(localEP, DnsTransportProtocol.Tcp, "DNS Server failed to bind.\r\n" + ex.ToString());

                    if (tcpListener != null)
                        tcpListener.Dispose();
                }

                if (_enableDnsOverHttp)
                {
                    IPEndPoint httpEP = new IPEndPoint(localEP.Address, 8053);
                    Socket httpListener = null;

                    try
                    {
                        httpListener = new Socket(httpEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                        httpListener.Bind(httpEP);
                        httpListener.Listen(100);

                        _httpListeners.Add(httpListener);

                        _isDnsOverHttpsEnabled = true;

                        LogManager log = _log;
                        if (log != null)
                            log.Write(httpEP, "Http", "DNS Server was bound successfully.");
                    }
                    catch (Exception ex)
                    {
                        LogManager log = _log;
                        if (log != null)
                            log.Write(httpEP, "Http", "DNS Server failed to bind.\r\n" + ex.ToString());

                        if (httpListener != null)
                            httpListener.Dispose();
                    }
                }

                if (_enableDnsOverTls && (_certificate != null))
                {
                    IPEndPoint tlsEP = new IPEndPoint(localEP.Address, 853);
                    Socket tlsListener = null;

                    try
                    {
                        tlsListener = new Socket(tlsEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                        tlsListener.Bind(tlsEP);
                        tlsListener.Listen(100);

                        _tlsListeners.Add(tlsListener);

                        LogManager log = _log;
                        if (log != null)
                            log.Write(tlsEP, DnsTransportProtocol.Tls, "DNS Server was bound successfully.");
                    }
                    catch (Exception ex)
                    {
                        LogManager log = _log;
                        if (log != null)
                            log.Write(tlsEP, DnsTransportProtocol.Tls, "DNS Server failed to bind.\r\n" + ex.ToString());

                        if (tlsListener != null)
                            tlsListener.Dispose();
                    }
                }

                if (_enableDnsOverHttps)
                {
                    //bind to http port 80 for certbot webroot support
                    {
                        IPEndPoint httpEP = new IPEndPoint(localEP.Address, 80);
                        Socket httpListener = null;

                        try
                        {
                            httpListener = new Socket(httpEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                            httpListener.Bind(httpEP);
                            httpListener.Listen(100);

                            _httpListeners.Add(httpListener);

                            LogManager log = _log;
                            if (log != null)
                                log.Write(httpEP, "Http", "DNS Server was bound successfully.");
                        }
                        catch (Exception ex)
                        {
                            LogManager log = _log;
                            if (log != null)
                                log.Write(httpEP, "Http", "DNS Server failed to bind.\r\n" + ex.ToString());

                            if (httpListener != null)
                                httpListener.Dispose();
                        }
                    }

                    //bind to https port 443
                    if (_certificate != null)
                    {
                        IPEndPoint httpsEP = new IPEndPoint(localEP.Address, 443);
                        Socket httpsListener = null;

                        try
                        {
                            httpsListener = new Socket(httpsEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                            httpsListener.Bind(httpsEP);
                            httpsListener.Listen(100);

                            _httpsListeners.Add(httpsListener);

                            _isDnsOverHttpsEnabled = true;

                            LogManager log = _log;
                            if (log != null)
                                log.Write(httpsEP, DnsTransportProtocol.Https, "DNS Server was bound successfully.");
                        }
                        catch (Exception ex)
                        {
                            LogManager log = _log;
                            if (log != null)
                                log.Write(httpsEP, DnsTransportProtocol.Https, "DNS Server failed to bind.\r\n" + ex.ToString());

                            if (httpsListener != null)
                                httpsListener.Dispose();
                        }
                    }
                }
            }

            //start reading query packets
            int listenerTaskCount = Math.Max(1, Environment.ProcessorCount);

            foreach (Socket udpListener in _udpListeners)
            {
                for (int i = 0; i < listenerTaskCount; i++)
                {
                    _ = Task.Factory.StartNew(delegate ()
                    {
                        return ReadUdpRequestAsync(udpListener);
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Current);
                }
            }

            foreach (Socket tcpListener in _tcpListeners)
            {
                for (int i = 0; i < listenerTaskCount; i++)
                {
                    _ = Task.Factory.StartNew(delegate ()
                    {
                        return AcceptConnectionAsync(tcpListener, DnsTransportProtocol.Tcp, false);
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Current);
                }
            }

            foreach (Socket httpListener in _httpListeners)
            {
                for (int i = 0; i < listenerTaskCount; i++)
                {
                    _ = Task.Factory.StartNew(delegate ()
                    {
                        return AcceptConnectionAsync(httpListener, DnsTransportProtocol.Https, false);
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Current);
                }
            }

            foreach (Socket tlsListener in _tlsListeners)
            {
                for (int i = 0; i < listenerTaskCount; i++)
                {
                    _ = Task.Factory.StartNew(delegate ()
                    {
                        return AcceptConnectionAsync(tlsListener, DnsTransportProtocol.Tls, false);
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Current);
                }
            }

            foreach (Socket httpsListener in _httpsListeners)
            {
                for (int i = 0; i < listenerTaskCount; i++)
                {
                    _ = Task.Factory.StartNew(delegate ()
                    {
                        return AcceptConnectionAsync(httpsListener, DnsTransportProtocol.Https, true);
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Current);
                }
            }

            _cachePrefetchSamplingTimer = new Timer(CachePrefetchSamplingTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
            _cachePrefetchRefreshTimer = new Timer(CachePrefetchRefreshTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
            _cacheMaintenanceTimer = new Timer(CacheMaintenanceTimerCallback, null, CACHE_MAINTENANCE_TIMER_INITIAL_INTEVAL, CACHE_MAINTENANCE_TIMER_PERIODIC_INTERVAL);

            _state = ServiceState.Running;

            UpdateThisServer();
            ResetPrefetchTimers();
        }

        public void Stop()
        {
            if (_state != ServiceState.Running)
                return;

            _state = ServiceState.Stopping;

            lock (_cachePrefetchSamplingTimerLock)
            {
                if (_cachePrefetchSamplingTimer != null)
                {
                    _cachePrefetchSamplingTimer.Dispose();
                    _cachePrefetchSamplingTimer = null;
                }
            }

            lock (_cachePrefetchRefreshTimerLock)
            {
                if (_cachePrefetchRefreshTimer != null)
                {
                    _cachePrefetchRefreshTimer.Dispose();
                    _cachePrefetchRefreshTimer = null;
                }
            }

            if (_cacheMaintenanceTimer != null)
            {
                _cacheMaintenanceTimer.Dispose();
                _cacheMaintenanceTimer = null;
            }

            foreach (Socket udpListener in _udpListeners)
                udpListener.Dispose();

            foreach (Socket tcpListener in _tcpListeners)
                tcpListener.Dispose();

            foreach (Socket httpListener in _httpListeners)
                httpListener.Dispose();

            foreach (Socket tlsListener in _tlsListeners)
                tlsListener.Dispose();

            foreach (Socket httpsListener in _httpsListeners)
                httpsListener.Dispose();

            _udpListeners.Clear();
            _tcpListeners.Clear();
            _httpListeners.Clear();
            _tlsListeners.Clear();
            _httpsListeners.Clear();

            _state = ServiceState.Stopped;
        }

        public async Task<DnsDatagram> DirectQueryAsync(DnsQuestionRecord question, int timeout = 2000)
        {
            try
            {
                Task<DnsDatagram> task = ProcessQueryAsync(new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { question }), new IPEndPoint(IPAddress.Any, 0), true, DnsTransportProtocol.Tcp);

                using (CancellationTokenSource timeoutCancellationTokenSource = new CancellationTokenSource())
                {
                    if (await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token)) != task)
                        return null;

                    timeoutCancellationTokenSource.Cancel(); //stop delay task
                }

                return await task;
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(ex);

                return null;
            }
        }

        #endregion

        #region properties

        public string ServerDomain
        {
            get { return _serverDomain; }
            set
            {
                if (!_serverDomain.Equals(value))
                {
                    _serverDomain = value.ToLower();

                    _authZoneManager.ServerDomain = _serverDomain;
                    _allowedZoneManager.ServerDomain = _serverDomain;
                    _blockedZoneManager.ServerDomain = _serverDomain;
                    _blockListZoneManager.ServerDomain = _serverDomain;

                    UpdateThisServer();
                }
            }
        }

        public string ConfigFolder
        { get { return _configFolder; } }

        public IReadOnlyList<IPEndPoint> LocalEndPoints
        {
            get { return _localEndPoints; }
            set { _localEndPoints = value; }
        }

        public NameServerAddress ThisServer
        { get { return _thisServer; } }

        public bool EnableDnsOverHttp
        {
            get { return _enableDnsOverHttp; }
            set { _enableDnsOverHttp = value; }
        }

        public bool EnableDnsOverTls
        {
            get { return _enableDnsOverTls; }
            set { _enableDnsOverTls = value; }
        }

        public bool EnableDnsOverHttps
        {
            get { return _enableDnsOverHttps; }
            set { _enableDnsOverHttps = value; }
        }

        public bool IsDnsOverHttpsEnabled
        { get { return _isDnsOverHttpsEnabled; } }

        public X509Certificate2 Certificate
        {
            get { return _certificate; }
            set
            {
                if (!value.HasPrivateKey)
                    throw new ArgumentException("Tls certificate does not contain private key.");

                _certificate = value;
            }
        }

        public AuthZoneManager AuthZoneManager
        { get { return _authZoneManager; } }

        public AllowedZoneManager AllowedZoneManager
        { get { return _allowedZoneManager; } }

        public BlockedZoneManager BlockedZoneManager
        { get { return _blockedZoneManager; } }

        public BlockListZoneManager BlockListZoneManager
        { get { return _blockListZoneManager; } }

        public CacheZoneManager CacheZoneManager
        { get { return _cacheZoneManager; } }

        public IDnsCache DnsCache
        { get { return _dnsCache; } }

        public bool AllowRecursion
        {
            get { return _allowRecursion; }
            set { _allowRecursion = value; }
        }

        public bool AllowRecursionOnlyForPrivateNetworks
        {
            get { return _allowRecursionOnlyForPrivateNetworks; }
            set { _allowRecursionOnlyForPrivateNetworks = value; }
        }

        public NetProxy Proxy
        {
            get { return _proxy; }
            set { _proxy = value; }
        }

        public IReadOnlyList<NameServerAddress> Forwarders
        {
            get { return _forwarders; }
            set { _forwarders = value; }
        }

        public bool PreferIPv6
        {
            get { return _preferIPv6; }
            set { _preferIPv6 = value; }
        }

        public bool RandomizeName
        {
            get { return _randomizeName; }
            set { _randomizeName = value; }
        }

        public int ForwarderRetries
        {
            get { return _forwarderRetries; }
            set
            {
                if (value > 0)
                    _forwarderRetries = value;
            }
        }

        public int ResolverRetries
        {
            get { return _resolverRetries; }
            set
            {
                if (value > 0)
                    _resolverRetries = value;
            }
        }

        public int ForwarderTimeout
        {
            get { return _forwarderTimeout; }
            set
            {
                if (value >= 2000)
                    _forwarderTimeout = value;
            }
        }

        public int ResolverTimeout
        {
            get { return _resolverTimeout; }
            set
            {
                if (value >= 2000)
                    _resolverTimeout = value;
            }
        }

        public int ClientTimeout
        {
            get { return _clientTimeout; }
            set
            {
                if (value >= 2000)
                    _clientTimeout = value;
            }
        }

        public int ForwarderConcurrency
        {
            get { return _forwarderConcurrency; }
            set { _forwarderConcurrency = value; }
        }

        public int ResolverMaxStackCount
        {
            get { return _resolverMaxStackCount; }
            set { _resolverMaxStackCount = value; }
        }

        public bool ServeStale
        {
            get { return _serveStale; }
            set { _serveStale = value; }
        }

        public int CachePrefetchEligibility
        {
            get { return _cachePrefetchEligibility; }
            set
            {
                if (value < 2)
                    throw new ArgumentOutOfRangeException("CachePrefetchEligibility", "Valid value is greater that or equal to 2.");

                _cachePrefetchEligibility = value;
            }
        }

        public int CachePrefetchTrigger
        {
            get { return _cachePrefetchTrigger; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException("CachePrefetchTrigger", "Valid value is greater that or equal to 0.");

                if (_cachePrefetchTrigger != value)
                {
                    _cachePrefetchTrigger = value;
                    ResetPrefetchTimers();
                }
            }
        }

        public int CachePrefetchSampleIntervalInMinutes
        {
            get { return _cachePrefetchSampleIntervalInMinutes; }
            set
            {
                if ((value < 1) || (value > 60))
                    throw new ArgumentOutOfRangeException("CacheRefreshSampleIntervalInMinutes", "Valid range is between 1 and 60 minutes.");

                if (_cachePrefetchSampleIntervalInMinutes != value)
                {
                    _cachePrefetchSampleIntervalInMinutes = value;
                    ResetPrefetchTimers();
                }
            }
        }

        public int CachePrefetchSampleEligibilityHitsPerHour
        {
            get { return _cachePrefetchSampleEligibilityHitsPerHour; }
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException("CachePrefetchSampleEligibilityHitsPerHour", "Valid value is greater than or equal to 1.");

                _cachePrefetchSampleEligibilityHitsPerHour = value;
            }
        }

        public LogManager LogManager
        {
            get { return _log; }
            set { _log = value; }
        }

        public LogManager QueryLogManager
        {
            get { return _queryLog; }
            set { _queryLog = value; }
        }

        public StatsManager StatsManager
        { get { return _stats; } }

        public int TcpSendTimeout
        {
            get { return _tcpSendTimeout; }
            set { _tcpSendTimeout = value; }
        }

        public int TcpReceiveTimeout
        {
            get { return _tcpReceiveTimeout; }
            set { _tcpReceiveTimeout = value; }
        }

        #endregion

        class CacheRefreshSample
        {
            public CacheRefreshSample(DnsQuestionRecord sampleQuestion, IReadOnlyList<NameServerAddress> viaNameServers, IReadOnlyList<NameServerAddress> viaForwarders)
            {
                SampleQuestion = sampleQuestion;
                ViaNameServers = viaNameServers;
                ViaForwarders = viaForwarders;
            }

            public DnsQuestionRecord SampleQuestion { get; }
            public IReadOnlyList<NameServerAddress> ViaNameServers { get; }
            public IReadOnlyList<NameServerAddress> ViaForwarders { get; }
        }
    }
}
