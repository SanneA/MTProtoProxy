﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MTProtoProxy
{
    internal class MTPSocket : IDisposable
    {
        public long SessionId { get => _sessionId; }
        public IPEndPoint IPEndPoint { get => _ipEndPoint; }
        public bool IsConnected { get => _isConnected; }
        public bool IsClosed { get => _isDisposed; }
        private readonly long _sessionId;
        private Socket _socket;
        private readonly IPEndPoint _ipEndPoint;
        private Thread _thread;
        private readonly object _lockSend = new object();
        private readonly object _lockConnect = new object();
        private volatile bool _isConnected;
        private volatile bool _isDisposed;
        private readonly MTProtoPacket _mtprotoPacket;
        public event EventHandler<byte[]> PacketReceived;
        public event EventHandler ReceiverEnded;
        public MTPSocket(IPEndPoint ipEndPoint, long sessionId)
        {
            _ipEndPoint = ipEndPoint;
            _sessionId = sessionId;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _mtprotoPacket = new MTProtoPacket();
        }
        public bool Connect()
        {
            lock (_lockConnect)
            {
                ThrowIfDisposed();
                if (!_isConnected)
                {
                    try
                    {
                        var task = Task.Run(() => _socket.ConnectAsync(_ipEndPoint).GetAwaiter().GetResult());
                        if (!task.Wait(2000))
                        {
                            return false;
                        }
                        lock (_lockSend)
                        {
                            _mtprotoPacket.Clear();
                            var buffer = _mtprotoPacket.GetInitBufferObfuscated2();
                            _socket.SendAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult();
                        }
                        _thread = new Thread(async () => await StartReceiverAsync().ConfigureAwait(false));
                        _thread.Start();
                        _isConnected = true;
                        return true;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        return false;
                    }
                }
                return true;
            }
        }
        public Task SendAsync(byte[] buffer)
        {
            lock (_lockSend)
            {
                ThrowIfDisposed();
                if (_isConnected)
                {
                    try
                    {
                        var packet = _mtprotoPacket.CreatePacketObfuscated2(buffer);
                        return _socket.SendAsync(packet, 0, packet.Length);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                return null;
            }
        }
        private async Task<bool> ReceiveAsync(byte[] buffer)
        {
            var failedcomplete = false;

            var bytesRead = 0;
            do
            {
                var result = await _socket.ReceiveAsync(buffer, bytesRead, buffer.Length - bytesRead).ConfigureAwait(false);
                if (result == 0)
                {
                    failedcomplete = true;
                    break;
                }
                bytesRead += result;

            } while (bytesRead != buffer.Length);

            return failedcomplete;
        }
        private async Task<byte[]> ReceiveAsync()
        {
            ThrowIfDisposed();
            var packetLengthBytes = new byte[1];
            var resultPacketLength = await ReceiveAsync(packetLengthBytes).ConfigureAwait(false);
            if (resultPacketLength)
            {
                return null;
            }
            packetLengthBytes = _mtprotoPacket.DecryptObfuscated2(packetLengthBytes);

            var packetLength = BitConverter.ToInt32(packetLengthBytes.Concat(new byte[] { 0x00, 0x00, 0x00 }).ToArray(), 0);

            int lengthBytes;
            if (packetLength < 0x7F)
            {
                lengthBytes = packetLength << 2;
            }
            else
            {
                var lenBytes = new byte[3];
                var resultLengthBytes = await ReceiveAsync(lenBytes).ConfigureAwait(false);
                if (resultLengthBytes)
                {
                    return null;
                }
                lenBytes = _mtprotoPacket.DecryptObfuscated2(lenBytes);
                lengthBytes = BitConverter.ToInt32(lenBytes.Concat(new byte[] { 0x00 }).ToArray(), 0) << 2;
            }
            var packetBytes = new byte[lengthBytes];
            var resultpacket = await ReceiveAsync(packetBytes).ConfigureAwait(false);
            if (resultpacket)
            {
                return null;
            }
            packetBytes = _mtprotoPacket.DecryptObfuscated2(packetBytes);
            return packetBytes;
        }
        private async Task StartReceiverAsync()
        {
            while (_socket.IsConnected())
            {
                try
                {
                    var result = await ReceiveAsync().ConfigureAwait(false);
                    if (result is null)
                    {
                        break;
                    }
                    PacketReceived?.BeginInvoke(this, result, null, null);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error receiving: " + e);
                    break;
                }
                Thread.Sleep(50);
            }
            ReceiverEnded.BeginInvoke(this, null, null, null);
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool isDisposing)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;

            if (!isDisposing)
            {
                return;
            }
            if (_thread != null)
            {
                try
                {
                    _thread.Abort();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                _thread = null;
            }
            if (_socket != null)
            {
                try
                {
                    _socket.Shutdown(SocketShutdown.Both);
                    _socket.Disconnect(false);
                    _socket.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    _socket = null;
                }
            }
            _isConnected = false;
        }
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException("Connection was disposed.");
            }
        }
    }
}