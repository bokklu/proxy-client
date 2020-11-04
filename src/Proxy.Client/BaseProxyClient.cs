﻿using Proxy.Client.Contracts;
using Proxy.Client.Contracts.Constants;
using Proxy.Client.Exceptions;
using Proxy.Client.Utilities;
using Proxy.Client.Utilities.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Proxy.Client
{
    public abstract class BaseProxyClient : IProxyClient
    {
        public string ProxyHost { get; protected set; }
        public int ProxyPort { get; protected set; }

        protected internal Socket Socket { get; private set; }
        protected internal bool IsConnected { get; private set; }
        protected internal string DestinationHost { get; private set; }
        protected internal int DestinationPort { get; private set; }

        private SslStream _sslStream;

        public abstract ProxyResponse Get(string destinationHost, int destinationPort, IDictionary<string, string> headers = null, bool isSsl = false);
        public abstract Task<ProxyResponse> GetAsync(string destinationHost, int destinationPort, IDictionary<string, string> headers = null, bool isSsl = false);
        public abstract ProxyResponse Post(string destinationHost, int destinationPort, string body, IDictionary<string, string> headers = null, bool isSsl = false);
        public abstract Task<ProxyResponse> PostAsync(string destinationHost, int destinationPort, string body, IDictionary<string, string> headers = null, bool isSsl = false);

        protected internal abstract void SendConnectCommand(bool isSsl);
        protected internal abstract Task SendConnectCommandAsync(bool isSsl);
        protected internal abstract void HandleProxyCommandError(byte[] response);

        protected internal ProxyResponse HandleRequest(Action notConnectedAtn, Func<(ProxyResponse response, float firstByteTime)> connectedFn,
            string destinationHost, int destinationPort)
        {
            try
            {
                if (String.IsNullOrEmpty(destinationHost))
                    throw new ArgumentNullException(nameof(destinationHost));

                if (destinationPort <= 0 || destinationPort > 65535)
                    throw new ArgumentOutOfRangeException(nameof(destinationPort),
                        "Destination port must be greater than zero and less than 65535");

                float connectTime = 0;

                if (!IsConnected)
                {
                    DestinationHost = destinationHost;
                    DestinationPort = destinationPort;

                    connectTime = TimingHelper.Measure(() =>
                    {
                        Socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        Socket.Connect(ProxyHost, ProxyPort);
                        notConnectedAtn();
                    });

                    IsConnected = true;
                }

                var (time, innerResult) = TimingHelper.Measure(() =>
                {
                    return connectedFn();
                });

                innerResult.response.Timings.ConnectTime = connectTime;
                innerResult.response.Timings.ResponseTime = connectTime + time;
                innerResult.response.Timings.FirstByteTime = innerResult.firstByteTime;

                return innerResult.response;
            }
            catch (Exception)
            {
                throw new ProxyException(String.Format(CultureInfo.InvariantCulture,
                    $"Connection to proxy host {ProxyHost} on port {ProxyPort} failed."));
            }
        }

        protected internal async Task<ProxyResponse> HandleRequestAsync(Func<Task> notConnectedFn, Func<Task<(ProxyResponse response, float firstByteTime)>> connectedFn,
            string destinationHost, int destinationPort)
        {
            try
            {
                if (String.IsNullOrEmpty(destinationHost))
                    throw new ArgumentNullException(nameof(destinationHost));

                if (destinationPort <= 0 || destinationPort > 65535)
                    throw new ArgumentOutOfRangeException(nameof(destinationPort),
                        "Destination port must be greater than zero and less than 65535");

                float connectTime = 0;

                if (!IsConnected)
                {
                    DestinationHost = destinationHost;
                    DestinationPort = destinationPort;

                    connectTime = await TimingHelper.MeasureAsync(async () =>
                    {
                        Socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        await Socket.ConnectAsync(ProxyHost, ProxyPort);
                        await notConnectedFn();
                    });

                    IsConnected = true;
                }

                var (time, innerResult) = await TimingHelper.MeasureAsync(async () =>
                {
                    return await connectedFn();
                });

                innerResult.response.Timings.ConnectTime = connectTime;
                innerResult.response.Timings.ResponseTime = connectTime + time;
                innerResult.response.Timings.FirstByteTime = connectTime + innerResult.firstByteTime;

                return innerResult.response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);

                throw new ProxyException(String.Format(CultureInfo.InvariantCulture,
                    $"Connection to proxy host {ProxyHost} on port {ProxyPort} failed."));
            }
        }

        protected internal void HandleConnectCommand(Action atn, bool isSsl)
        {
            atn();

            if (isSsl)
                HandleSslHandshake();
        }

        protected internal async Task HandleConnectCommandAsync(Func<Task> fn, bool isSsl)
        {
            await fn();

            if (isSsl)
                await HandleSslHandshakeAsync();
        }

        protected internal (ProxyResponse response, float firstByteTime) SendGetCommand(IDictionary<string, string> headers, bool isSsl)
        {
            string response;
            float firstByteTime;

            if (isSsl)
            {
                var writeBuffer = RequestHelper.GetCommand(DestinationHost, RequestConstants.SSL, headers);
                _sslStream.Write(writeBuffer);

                (response, firstByteTime) = _sslStream.ReadAll();
            }
            else
            {
                var writeBuffer = RequestHelper.GetCommand(DestinationHost, RequestConstants.NO_SSL, headers);
                Socket.Send(writeBuffer);

                (response, firstByteTime) = Socket.ReceiveAll(SocketFlags.None);
            }
            
            return (ResponseBuilder.BuildProxyResponse(response), firstByteTime);
        }

        protected internal async Task<(ProxyResponse response, float firstByteTime)> SendGetCommandAsync(IDictionary<string, string> headers, bool isSsl)
        {
            string response;
            float firstByteTime;

            if (isSsl)
            {
                var writeBuffer = RequestHelper.GetCommand(DestinationHost, RequestConstants.SSL, headers);
                await _sslStream.WriteAsync(writeBuffer, 0, writeBuffer.Length);

                (response, firstByteTime) = await _sslStream.ReadAllAsync();
            }
            else
            {
                var writeBuffer = RequestHelper.GetCommand(DestinationHost, RequestConstants.NO_SSL, headers);
                await Socket.SendAsync(writeBuffer, SocketFlags.None);

                (response, firstByteTime) = await Socket.ReceiveAllAsync(SocketFlags.None);
            }

            return (ResponseBuilder.BuildProxyResponse(response), firstByteTime);
        }

        protected internal (ProxyResponse response, float firstByteTime) SendPostCommand(string body, IDictionary<string, string> headers, bool isSsl)
        {
            string response;
            float firstByteTime;

            if (isSsl)
            {
                var writeBuffer = RequestHelper.PostCommand(DestinationHost, body, RequestConstants.SSL, headers);
                _sslStream.Write(writeBuffer);

                (response, firstByteTime) = _sslStream.ReadAll();
            }
            else
            {
                var writeBuffer = RequestHelper.PostCommand(DestinationHost, body, RequestConstants.NO_SSL, headers);
                Socket.Send(writeBuffer);

                (response, firstByteTime) = Socket.ReceiveAll(SocketFlags.None);
            }

            return (ResponseBuilder.BuildProxyResponse(response), firstByteTime);
        }

        protected internal async Task<(ProxyResponse response, float firstByteTime)> SendPostCommandAsync(string body, IDictionary<string, string> headers, bool isSsl)
        {
            string response;
            float firstByteTime;

            if (isSsl)
            {
                var writeBuffer = RequestHelper.PostCommand(DestinationHost, body, RequestConstants.SSL, headers);
                await _sslStream.WriteAsync(writeBuffer, 0, writeBuffer.Length);

                (response, firstByteTime) = await _sslStream.ReadAllAsync();
            }
            else
            {
                var writeBuffer = RequestHelper.PostCommand(DestinationHost, body, RequestConstants.NO_SSL, headers);
                await Socket.SendAsync(writeBuffer, SocketFlags.None);

                (response, firstByteTime) = await Socket.ReceiveAllAsync(SocketFlags.None);
            }

            return (ResponseBuilder.BuildProxyResponse(response), firstByteTime);
        }

        #region Ssl Methods
        private void HandleSslHandshake()
        {
            var networkStream = new NetworkStream(Socket);
            _sslStream = new SslStream(networkStream, false, new RemoteCertificateValidationCallback(ValidateServerCertificate), null);

            _sslStream.AuthenticateAsClient(DestinationHost);
        }

        private async Task HandleSslHandshakeAsync()
        {
            var networkStream = new NetworkStream(Socket);
            _sslStream = new SslStream(networkStream, false, new RemoteCertificateValidationCallback(ValidateServerCertificate), null);

            await _sslStream.AuthenticateAsClientAsync(DestinationHost);
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain,
                                                  SslPolicyErrors sslPolicyErrors) => sslPolicyErrors == SslPolicyErrors.None ? true : false;

        #endregion

        public void Dispose()
        {
            Socket?.Close();
            _sslStream?.Dispose();
        }
    }
}
