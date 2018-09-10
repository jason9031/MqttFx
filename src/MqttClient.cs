﻿using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels.Sockets;
using nMqtt.Messages;
using nMqtt.Packets;
using nMqtt.Protocol;
using nMqtt.Extensions;

namespace nMqtt
{
    /// <summary>
    /// Mqtt客户端
    /// </summary>
    public class MqttClient
    {
        readonly ILogger _logger;
        readonly IEventLoopGroup _group;
        readonly MqttClientOptions _options;
        readonly MqttPacketIdentifierProvider _packetIdentifierProvider;
        readonly MqttPacketDispatcher _packetDispatcher;

        private IChannel _clientChannel;
        private CancellationTokenSource _cancellationTokenSource;

        public Action<Message> OnMessageReceived;
        public Action<ConnectReturnCode> OnConnected;

        public MqttClient(string clientId = default, ILogger logger = default)
        {
            _logger = logger ?? NullLogger<MqttClient>.Instance;
            _group = new MultithreadEventLoopGroup();
            _options = new MqttClientOptions();
            _packetIdentifierProvider = new MqttPacketIdentifierProvider();
            _packetDispatcher = new MqttPacketDispatcher();
        }

        /// <summary>
        /// 连接
        /// </summary>
        /// <returns></returns>
        public async Task<ConnectReturnCode> ConnectAsync()
        {
            var clientReadListener = new ReadListeningHandler();
            var bootstrap = new Bootstrap();
            bootstrap
                .Group(_group)
                .Channel<TcpSocketChannel>()
                .Option(ChannelOption.TcpNodelay, true)
                .Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
                {
                    var pipeline = channel.Pipeline;
                    pipeline.AddLast(MqttEncoder.Instance, new MqttDecoder(false, 256 * 0124), clientReadListener);
                }));

            try
            {
                _packetDispatcher.Reset();
                _packetIdentifierProvider.Reset();
                _cancellationTokenSource = new CancellationTokenSource();
                _clientChannel = await bootstrap.ConnectAsync(new IPEndPoint(IPAddress.Parse("118.126.96.166"), 1883));

                StartReceivingPackets(clientReadListener);

                var connectResponse = await AuthenticateAsync(clientReadListener); ;
                if (connectResponse.ConnectReturnCode == ConnectReturnCode.ConnectionAccepted)
                {
                    OnConnected?.Invoke(connectResponse.ConnectReturnCode);
                }
                return connectResponse.ConnectReturnCode;
            }
            catch
            {
                await DisconnectAsync();
                throw new Exception("BrokerUnavailable");
            }
        }

        private Task<ConnAckPacket> AuthenticateAsync(ReadListeningHandler readListener)
        {
            var packet = new ConnectPacket
            {
                ClientId = _options.ClientId,
                CleanSession = _options.CleanSession,
                KeepAlive = _options.KeepAlive,
            };
            if(_options.Credentials != null)
            {
                packet.UsernameFlag = true;
                packet.UserName = _options.Credentials.Username;
                packet.Password = _options.Credentials.Username;
            }
            return SendAndReceiveAsync<ConnAckPacket>(packet, _cancellationTokenSource.Token);
        }

        private void StartReceivingPackets(ReadListeningHandler clientReadListener)
        {
            Task.Run(() => ReceivePacketsAsync(clientReadListener));
        }

        private async Task ReceivePacketsAsync(ReadListeningHandler clientReadListener)
        {
            while (true)
            {
                if (await clientReadListener.ReceiveAsync() is Packet packet)
                {
                    await ProcessReceivedPacketAsync(packet);
                }
            }
        }

        private Task ProcessReceivedPacketAsync(Packet packet)
        {
            switch (packet)
            {
                case DisconnectPacket disconnectPacket:
                    return DisconnectAsync();

                case PingReqPacket pingReqPacket:
                    return _clientChannel.WriteAndFlushAsync(new PingRespPacket());

                case PublishPacket publishPacket:
                    return ProcessReceivedPublishPacketAsync(publishPacket);

                case PublishRelPacket publishRelPacket:
                    return Task.CompletedTask;
            }
            _packetDispatcher.Dispatch(packet);
            return Task.CompletedTask;
        }

        private Task ProcessReceivedPublishPacketAsync(PublishPacket publishPacket)
        {
            OnMessageReceived?.Invoke(new Message
            {
                Topic = publishPacket.TopicName,
                Payload = publishPacket.Payload,
                Qos = publishPacket.Qos,
                Retain = publishPacket.Retain
            });

            switch (publishPacket.Qos)
            {
                case MqttQos.AtMostOnce:
                    return Task.CompletedTask;
                case MqttQos.AtLeastOnce:
                    return _clientChannel.WriteAndFlushAsync(new PublishAckPacket
                    {
                        PacketIdentifier = publishPacket.PacketIdentifier
                    });
                case MqttQos.ExactlyOnce:
                    return _clientChannel.WriteAndFlushAsync(new PublishRecPacket
                    {
                        PacketIdentifier = publishPacket.PacketIdentifier
                    });
                default:
                    throw new Exception("Received a not supported QoS level.");
            }
        }

        private async Task<TResponsePacket> SendAndReceiveAsync<TResponsePacket>(Packet requestPacket, CancellationToken cancellationToken) where TResponsePacket : Packet
        {
            cancellationToken.ThrowIfCancellationRequested();

            ushort identifier = 0;
            if (requestPacket is IMqttPacketIdentifier packetWithIdentifier)
            {
                identifier = packetWithIdentifier.PacketIdentifier;
            }

            var packetAwaiter = _packetDispatcher.AddPacketAwaiter<TResponsePacket>(identifier);
            try
            {
                await _clientChannel.WriteAndFlushAsync(requestPacket);
                var respone = await Extensions.TaskExtensions.TimeoutAfterAsync(ct => packetAwaiter.Task, _options.Timeout, cancellationToken);
                return (TResponsePacket)respone;
            }
            catch (Exception)
            {

                throw;
            }
            finally
            {
                _packetDispatcher.RemovePacketAwaiter<TResponsePacket>(identifier);
            }
        }

        /// <summary>
        /// 发布消息
        /// </summary>
        /// <param name="topic">主题</param>
        /// <param name="payload">有效载荷</param>
        /// <param name="qos">服务质量等级</param>
        public Task PublishAsync(string topic, byte[] payload, MqttQos qos = MqttQos.AtMostOnce)
        {
            var packet = new PublishPacket(qos)
            {
                PacketIdentifier = _packetIdentifierProvider.GetNewPacketIdentifier(),
                TopicName = topic,
                Payload = payload
            };
            return _clientChannel.WriteAndFlushAsync(packet);
        }

        /// <summary>
        /// 订阅主题
        /// </summary>
        /// <param name="topic">主题</param>
        /// <param name="qos">服务质量等级</param>
        public Task SubscribeAsync(string topic, MqttQos qos = MqttQos.AtMostOnce)
        {
            var packet = new SubscribePacket
            {
                PacketIdentifier = _packetIdentifierProvider.GetNewPacketIdentifier(),
            };
            packet.Subscribe(topic, qos);
            return _clientChannel.WriteAndFlushAsync(packet);
        }

        /// <summary>
        /// 取消订阅
        /// </summary>
        /// <param name="topics">主题</param>
        public Task UnsubscribeAsync(params string[] topics)
        {
            var packet = new UnsubscribePacket();
            packet.AddRange(topics);
            return _clientChannel.WriteAndFlushAsync(packet);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        /// <returns></returns>
        public async Task DisconnectAsync()
        {
            await _clientChannel.CloseAsync();
            await _group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        }
    }
}
