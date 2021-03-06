﻿using SRConnection.Packet;
using SRConnection.Stun;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SRConnection
{

	internal class ConnectionImpl : IDisposable
	{
		public const int MaxBufferSize = 1200;

		public bool Disposed => m_Disposed;

		protected virtual bool IsHost => false;

		public virtual bool UseP2P => false;

		public int SelfId { get; protected set; }

		internal UdpSocket m_Socket;
		internal EncryptorGenerator m_EncryptorGenerator;
		bool m_Disposed = false;
		internal protected byte[] m_SendBuffer = new byte[MaxBufferSize];
		protected byte[] m_ReceiveBuffer = new byte[MaxBufferSize];
		TimeSpan m_PingTimer;

		protected CookieProvider m_CookieProvider = new CookieProvider();
		protected PeerManager m_PeerManager;
		public PeerToPeerTaskManager P2PTask { get; private set; }

		public Action<PeerEntry> OnAddPeer;

		public Action<PeerEntry> OnRemotePeer;

		public bool DisposeOnDisconnectOwner = true;

		internal ConnectionImpl(UdpSocket socket, EncryptorGenerator encryptorGenerator)
		{
			m_Socket = socket;
			m_EncryptorGenerator = encryptorGenerator;
			m_PeerManager = new PeerManager(this);
			P2PTask = new PeerToPeerTaskManager(this, m_CookieProvider, m_PeerManager);
			m_CookieProvider.Update();
		}

		~ConnectionImpl()
		{
			if (m_Disposed) return;
			m_Disposed = true;
			Dispose(false);
		}

		public void Dispose()
		{
			if (m_Disposed) return;
			m_Disposed = true;
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public ICollection<PeerEntry> GetPeers()
		{
			return m_PeerManager.GetPeers();
		}

		public virtual void OnUpdateStatus(TimeSpan delta)
		{
			PeerUpdate(delta);
		}

		protected virtual void PeerUpdate(TimeSpan delta)
		{
			m_PeerManager.Update(delta);
			P2PTask.Update(delta);
			if (!IsHost)
			{
				m_PingTimer -= delta;
				if (m_PingTimer < TimeSpan.Zero)
				{
					m_PingTimer = TimeSpan.FromSeconds(3);
					BroadcastPing();
				}
			}
		}

		internal protected virtual void OnAdd(PeerEntry peer)
		{
			Log.Debug("add peer id:{0}, ip", peer.ConnectionId, peer.EndPoint);
			OnAddPeer?.Invoke(peer);
		}

		internal protected virtual void OnRemove(PeerEntry peer)
		{
			Log.Debug("remove peer id:{0}, ip", peer.ConnectionId, peer.EndPoint);
			OnRemotePeer?.Invoke(peer);
		}

		protected virtual void Dispose(bool disposing)
		{
			Log.Debug("Start Dispose {0}", disposing);
			m_Socket?.Dispose();
			m_Socket = null;
			m_CookieProvider.Dispose();
			m_CookieProvider = null;
		}

		public bool Send(int connectionId, byte[] buf, bool encrypt = true)
		{
			return Send(connectionId, buf, 0, buf.Length, encrypt);
		}

		public bool Send(int connectionId, byte[] buf, int offset, int size, bool encrypt = true)
		{
			if (m_PeerManager.TryGetValue(connectionId, out PeerEntry peer))
			{
				SendImpl(peer, buf, offset, size, encrypt);
				return true;
			}
			return false;
		}

		void SendImpl(PeerEntry peer, byte[] buf, int offset, int size, bool encrypt)
		{
			Log.Trace("Send id:{0}, size:{1}, encrypt:{2}", peer.ConnectionId, size, encrypt);
			int packetSize;
			int id = GetSendId(peer.ConnectionId);
			if (encrypt)
			{
				var msg = new EncryptMessage(id, peer, new ArraySegment<byte>(buf, offset, size));
				packetSize = msg.Pack(m_SendBuffer, peer.Encryptor);
			}
			else
			{
				var msg = new PlainMessage(id, peer, new ArraySegment<byte>(buf, offset, size));
				packetSize = msg.Pack(m_SendBuffer);
			}
			m_Socket.Send(m_SendBuffer, 0, packetSize, peer.EndPoint);
		}

		protected virtual int GetSendId(int peerId)
		{
			return SelfId;
		}

		public bool SendPing(int connectionId)
		{
			Log.Trace("SendPing {0}", connectionId);
			if (m_PeerManager.TryGetValue(connectionId, out var peer))
			{
				SendPingImpl(peer);
				return true;
			}
			return false;
		}

		void SendPingImpl(PeerEntry peer)
		{
			var offset = new Ping(GetSendId(peer.ConnectionId), peer).Pack(m_SendBuffer, peer.Encryptor);
			m_Socket.Send(m_SendBuffer, 0, offset, peer.EndPoint);
		}

		Action<PeerEntry> m_SendPing;
		void BroadcastPing()
		{
			Log.Trace("BroadcastPing");
			if (m_SendPing == null) m_SendPing = SendPingImpl;
			m_PeerManager.ForEach(m_SendPing);
		}

		public bool SendDisconnect(int connectionId)
		{
			Log.Info("SendDisconnect:{0}", connectionId);
			if (m_PeerManager.TryGetValue(connectionId, out var peer))
			{
				var seq = peer.IncrementSendSequence();
				var msg = new Disconnect(GetSendId(peer.ConnectionId), seq);
				var size = msg.Pack(m_SendBuffer, peer.Encryptor);
				m_Socket.Send(m_SendBuffer, 0, size, peer.EndPoint);
				m_PeerManager.Remove(connectionId);
				return true;
			}
			return false;
		}

		public void BroadcastDisconnect()
		{
			Log.Info("BroadcastDisconnect");
			var peers = m_PeerManager.GetPeers().ToArray();
			foreach (var peer in peers)
			{
				var seq = peer.IncrementSendSequence();
				var msg = new Disconnect(GetSendId(peer.ConnectionId), seq);
				var size = msg.Pack(m_SendBuffer, peer.Encryptor);
				m_Socket.Send(m_SendBuffer, 0, size, peer.EndPoint);
			}
			foreach (var peer in peers)
			{
				m_PeerManager.Remove(peer.ConnectionId);
			}
		}

		public virtual void SendPeerToPeerList() { }

		public virtual bool TryGetPeerToPeerList(out PeerToPeerList list)
		{
			list = default;
			return false;
		}

		public bool TryReceiveFrom(byte[] buf, int offset, ref int size, ref int connectionId)
		{
			ArraySegment<byte> payload = default;
			if (!ReceiveImpl(m_ReceiveBuffer, ref connectionId, ref payload))
			{
				return false;
			}
			BinaryUtil.Write(payload, buf, ref offset);
			size = payload.Count;
			Log.Trace("TryReceiveFrom {0} : size {1}", connectionId, size);
			return true;
		}

		public bool Poll(int microSeconds)
		{
			Log.Trace("Poll microSeconds {0}", microSeconds);
			return m_Socket.Poll(microSeconds, SelectMode.SelectRead);
		}

		bool ReceiveImpl(byte[] buf, ref int connectionId, ref ArraySegment<byte> ret)
		{
			var soket = m_Socket;
			while (!m_Disposed)
			{
				IPEndPoint remoteEP = null;
				var size = 0;
				if (!soket.TryReceiveFrom(buf, ref size, ref remoteEP))
				{
					return false;
				}
				if (size == 0 || size >= buf.Length)
				{
					continue;
				}
				switch (buf[0])
				{
					case (byte)PacketType.ClientHello:
						OnClientHello(buf, size, remoteEP);
						break;
					case (byte)PacketType.HandshakeRequest:
						OnHandshakeRequest(buf, size, remoteEP);
						break;
					case (byte)PacketType.Disconnect:
						OnDisconnect(buf, size);
						break;
					case (byte)PacketType.PeerToPeerHello:
						OnPeerToPeerHello(buf, size, remoteEP);
						break;
					case (byte)PacketType.PeerToPeerRequest:
						OnPeerToPeerRequest(buf, size, remoteEP);
						break;
					case (byte)PacketType.PeerToPeerAccept:
						OnPeerToPeerAccept(buf, size, remoteEP);
						break;
					case (byte)PacketType.PeerToPeerList:
						OnPeerToPeerList(buf, size);
						break;
					case (byte)PacketType.Ping:
						OnPing(buf, size, remoteEP);
						break;
					case (byte)PacketType.Pong:
						OnPong(buf, size, remoteEP);
						break;
					case (byte)PacketType.PlainMessage:
						if (OnPlainMessage(buf, size, remoteEP, ref connectionId, ref ret))
						{
							return true;
						}
						break;
					case (byte)PacketType.EncryptMessage:
						if (OnEncryptMessage(buf, size, remoteEP, ref connectionId, ref ret))
						{
							return true;
						}
						break;
				}
				// 一度でも実行できていればDisposeは無視する
				if (m_Disposed)
				{
					return false;
				}
			}
			throw new ObjectDisposedException(GetType().FullName);
		}


		protected virtual void OnClientHello(byte[] buf, int size, IPEndPoint remoteEP) { }

		protected virtual void OnHandshakeRequest(byte[] buf, int size, IPEndPoint remoteEP) { }

		void OnDisconnect(byte[] buf, int size)
		{
			int offset = 1;
			var connectionId = BinaryUtil.ReadInt(buf, ref offset);
			if (!m_PeerManager.TryGetValue(connectionId, out var peer)) return;

			if (Disconnect.TryUnpack(buf, size, peer.Encryptor, out _))
			{
				Log.Debug("OnDisconnect {0}", connectionId);
				m_PeerManager.Remove(connectionId);
			}
		}

		void OnPing(byte[] buf, int size, IPEndPoint remoteEP)
		{
			int offset = 1;
			var connectionId = BinaryUtil.ReadInt(buf, ref offset);

			if (!m_PeerManager.TryGetValue(connectionId, out var peer)) return;

			if (!Ping.TryUnpack(buf, size, peer.Encryptor, out var packet)) return;

			peer.Update(remoteEP, packet.SendSequence, packet.ReceiveSequence);

			offset = new Pong(GetSendId(connectionId), peer).Pack(m_SendBuffer, peer.Encryptor);
			m_Socket.Send(m_SendBuffer, 0, offset, peer.EndPoint);
			Log.Trace("OnPing {0}", connectionId);
		}

		void OnPong(byte[] buf, int size, IPEndPoint remoteEP)
		{
			int offset = 1;
			var connectionId = BinaryUtil.ReadInt(buf, ref offset);
			if (!m_PeerManager.TryGetValue(connectionId, out PeerEntry peer)) return;

			if (!Pong.TryUnpack(buf, size, peer.Encryptor, out var packet)) return;

			peer.Update(remoteEP, packet.SendSequence, packet.ReceiveSequence);

			Log.Trace("OnPong {0}", connectionId);
		}

		bool OnEncryptMessage(byte[] buf, int size, IPEndPoint remoteEP, ref int connectionId, ref ArraySegment<byte> ret)
		{
			int offset = 1;
			connectionId = BinaryUtil.ReadInt(buf, ref offset);

			if (!m_PeerManager.TryGetValue(connectionId, out PeerEntry peer)) return false;

			if (!EncryptMessage.TryUnpack(buf, size, peer.Encryptor, out var packet)) return false;

			peer.Update(remoteEP, packet.SendSequence, packet.ReceiveSequence);

			ret = packet.Payload;

			return true;
		}

		bool OnPlainMessage(byte[] buf, int size, IPEndPoint remoteEP, ref int connectionId, ref ArraySegment<byte> ret)
		{
			int offset = 1;
			connectionId = BinaryUtil.ReadInt(buf, ref offset);

			if (!m_PeerManager.TryGetValue(connectionId, out PeerEntry peer)) return false;

			if (!peer.EndPoint.Equals(remoteEP)) return false;

			if (!PlainMessage.TryUnpack(buf, size, out var packet)) return false;

			ret = packet.Payload;

			return true;
		}

		void OnPeerToPeerRequest(byte[] buf, int size, IPEndPoint remoteEP)
		{
			if (PeerToPeerRequest.TryUnpack(m_CookieProvider, buf, size, out var packet))
			{
				Log.Debug("receive p2p request {0}", remoteEP);
				P2PTask.HandshakeAccept(packet, remoteEP);
			}
			else
			{
				Log.Warning("unpack fail p2p request {0}", remoteEP);
			}
		}

		void OnPeerToPeerAccept(byte[] buf, int size, IPEndPoint remoteEP)
		{
			Log.Debug("receive p2p accept {0}", remoteEP);
			P2PTask.HandshakeComplete(buf, size, remoteEP);
		}

		void OnPeerToPeerHello(byte[] buf, int size, IPEndPoint remoteEP)
		{
			if (!UseP2P) return;

			if (PeerToPeerHello.TryUnpack(buf, size, out var packet))
			{
				Log.Debug("receive p2p hello {0}", remoteEP);
				P2PTask.OnPeerToPeerHello(packet, remoteEP);
			}
			else
			{
				Log.Warning("unpack fail p2p hello {0}", remoteEP);
			}
		}

		void OnPeerToPeerList(byte[] buf, int size)
		{
			if (IsHost || !UseP2P) return;
			P2PTask.OnPeerToPeerList(buf, size);
		}

		public Task<StunResult> StunQuery(string host, int port, TimeSpan timeout)
		{
			return m_Socket.StunQuery(host, port, timeout);
		}

	}

}