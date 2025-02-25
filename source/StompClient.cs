﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Netina.Stomp.Client.Interfaces;
using Netina.Stomp.Client.Messages;
using Netina.Stomp.Client.Utils;
using Newtonsoft.Json;
using Websocket.Client;
using Websocket.Client.Models;

namespace Netina.Stomp.Client
{
    public class StompClient : IStompClient
    {
        public event EventHandler<StompMessage> OnConnect;
        public event EventHandler<DisconnectionInfo> OnClose;
        public event EventHandler<StompMessage> OnMessage;
        public event EventHandler<ReconnectionInfo> OnReconnect;
        public event EventHandler<StompMessage> OnError;

        public StompConnectionState StompState { get; private set; } = StompConnectionState.Closed;
        public string Version { get; private set; }

        private readonly WebsocketClient _socket;
        private readonly StompMessageSerializer _stompSerializer = new StompMessageSerializer();
        private readonly IDictionary<string, EventHandler<StompMessage>> _subscribers = new Dictionary<string, EventHandler<StompMessage>>();
        private readonly IDictionary<string, string> _connectingHeaders = new Dictionary<string, string>();

        /// <summary>
        /// StompClient Ctor
        /// </summary>
        /// <param name="url">Url of stomp websocket, start with wss or ws</param>
        /// <param name="reconnectEnable">Set reconnect enable or disable</param>
        /// <param name="stompVersion">Add stomp version in header for connecting, set "1.0,1.1,1.2" if nothing specified</param>
        /// <param name="reconnectTimeOut">Time range in ms, how long to wait before reconnecting if last reconnection failed. Set null to disable this feature</param>
        /// <param name="heartBeat">Set 0,1000 if nothing specified</param>
        public StompClient(string url, bool reconnectEnable = true, string stompVersion = null, TimeSpan? reconnectTimeOut = null, string heartBeat = null)
        {
            _socket = new WebsocketClient(new Uri(url), () => {
                var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("stomp");
                return ws;
            })
            {
                ReconnectTimeout = reconnectTimeOut,
                IsReconnectionEnabled = reconnectEnable,
                ErrorReconnectTimeout = TimeSpan.FromSeconds(2.0),
            };

            _socket.MessageReceived.Subscribe(HandleMessage);
            _socket.DisconnectionHappened.Subscribe(info =>
            {
                StompState = StompConnectionState.Closed;
                OnClose?.Invoke(this, info);
                _subscribers.Clear();
            });
            _socket.ReconnectionHappened.Subscribe(async info =>
            {
                if (info.Type == ReconnectionType.Initial)
                    return;
                OnReconnect?.Invoke(this, info);
                StompState = StompConnectionState.Reconnecting;
                await Reconnect();
            });

            _connectingHeaders.Add("accept-version", string.IsNullOrEmpty(stompVersion) ? "1.0,1.1,1.2" : stompVersion);
            _connectingHeaders.Add("heart-beat", string.IsNullOrEmpty(heartBeat) ? "0,1000" : heartBeat);

            OnConnect += (sender, message) =>
            {
                Version = message.Headers["version"];
            };
        }

        public async Task ConnectAsync(IDictionary<string, string> headers)
        {
            if (!_socket.IsRunning)
                await _socket.Start();
            if (!_socket.IsRunning)
                throw new Exception("Connection is not open");
            if (StompState != StompConnectionState.Closed)
                return;
            foreach (var header in headers)
            {
                _connectingHeaders.Add(header);
            }
            var connectMessage = new StompMessage(StompCommand.Connect, _connectingHeaders);
            await _socket.SendInstant(_stompSerializer.Serialize(connectMessage));
            StompState = StompConnectionState.Open;
        }

        public async Task Reconnect()
        {
            if (!_socket.IsRunning)
                await _socket.Start();
            if (StompState == StompConnectionState.Open)
                return;
            var connectMessage = new StompMessage(StompCommand.Connect, _connectingHeaders);
            await _socket.SendInstant(_stompSerializer.Serialize(connectMessage));
            StompState = StompConnectionState.Open;
        }

        public async Task SendAsync(object body, string destination, IDictionary<string, string> headers)
        {
            var jsonPayload = JsonConvert.SerializeObject(body);
            headers.Add("content-type", "application/json;charset=UTF-8");
            headers.Add("content-length", Encoding.UTF8.GetByteCount(jsonPayload).ToString());
            await SendAsync(jsonPayload, destination, headers);
        }

        public async Task SendAsync(string body, string destination, IDictionary<string, string> headers)
        {
            if (StompState != StompConnectionState.Open)
                await Reconnect();

            headers.Add("destination", destination);
            var connectMessage = new StompMessage(StompCommand.Send, body, headers);
            await _socket.SendInstant(_stompSerializer.Serialize(connectMessage));
        }

        public async Task SubscribeAsync<T>(string topic, IDictionary<string, string> headers, EventHandler<T> handler)
        {
            await SubscribeAsync(topic, headers, (sender, message) => handler(this, JsonConvert.DeserializeObject<T>(message.Body)));
        }

        public async Task SubscribeAsync(string topic, IDictionary<string, string> headers, EventHandler<StompMessage> handler)
        {
            if (StompState != StompConnectionState.Open)
                await Reconnect();

            headers.Add("destination", topic);
            headers.Add("id", $"sub-{_subscribers.Count}");
            var subscribeMessage = new StompMessage(StompCommand.Subscribe, headers);
            await _socket.SendInstant(_stompSerializer.Serialize(subscribeMessage));
            _subscribers.Add(topic, handler);
        }

        public async Task AckAsync(string id, string transaction = null)
        {
            await Acknowledge(true, id, transaction);
        }

        public async Task NackAsync(string id, string transaction = null)
        {
            await Acknowledge(false, id, transaction);
        }

        public async Task DisconnectAsync()
        {
            var connectMessage = new StompMessage(StompCommand.Disconnect);
            await _socket.SendInstant(_stompSerializer.Serialize(connectMessage));
            StompState = StompConnectionState.Closed;
            _socket.Dispose();
            _subscribers.Clear();
        }

        public void Dispose()
        {
            StompState = StompConnectionState.Closed;
            ((IDisposable)_socket).Dispose();
            _subscribers.Clear();
        }

        private async Task Acknowledge(bool isPositive, string id, string transaction = null)
        {
            if (StompState != StompConnectionState.Open)
                await Reconnect();

            var headers = new Dictionary<string, string>()
            {
                { "id", id }
            };
            if (string.IsNullOrEmpty(transaction))
                headers.Add("transaction", transaction);
            var connectMessage = new StompMessage(isPositive ? StompCommand.Ack : StompCommand.Nack, headers);
            await _socket.SendInstant(_stompSerializer.Serialize(connectMessage));
        }

        private void HandleMessage(ResponseMessage messageEventArgs)
        {
            var message = _stompSerializer.Deserialize(messageEventArgs.Text);
            OnMessage?.Invoke(this, message);
            if (message.Command == StompCommand.Connected)
                OnConnect?.Invoke(this, message);
            if (message.Command == StompCommand.Error)
                OnError?.Invoke(this, message);
            if (message.Headers.ContainsKey("destination"))
                _subscribers[message.Headers["destination"]](this, message);
        }
    }
}
