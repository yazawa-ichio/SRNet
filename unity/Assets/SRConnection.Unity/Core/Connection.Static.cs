using SRConnection.Packet;
using SRConnection.Stun;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SRConnection
{
	public partial class Connection : IDisposable
	{
		public static Connection StartServer(ServerConfig config)
		{
			return new Connection(new ServerConnectionImpl(config));
		}

		public static P2PHostConnection StartLocalHost(string roomName)
		{
			return StartLocalHost(new LocalHostConfig { RoomName = roomName });
		}

		public static P2PHostConnection StartLocalHost(LocalHostConfig config)
		{
			var impl = new P2PConnectionImpl(config);
			return new P2PHostConnection(impl);
		}

		public static Task<ClientConnection> ConnectToServer(ServerConnectSettings settings)
		{
			return ConnectToServer(settings, CancellationToken.None);
		}

		public static async Task<ClientConnection> ConnectToServer(ServerConnectSettings settings, CancellationToken token)
		{
			return new ClientConnection(await new ConnectToServerTask(settings, token).Run());
		}

		public static Task<Connection> ConnectToRoom(DiscoveryRoom room)
		{
			return ConnectToRoom(room, true, CancellationToken.None);
		}

		public static async Task<Connection> ConnectToRoom(DiscoveryRoom room, bool waitAllHandshake = true, CancellationToken token = default)
		{
			var remoteEP = new IPEndPoint(room.Address, room.Port);
			if (!PeerToPeerRoomData.TryUnpack(room.Data, room.Data.Length, out var data))
			{
				throw new Exception("unpack room info");
			}
			var impl = await new ConnectToLocalOwnerTask(remoteEP, data, room.DiscoveryPort, token).Run();
			return await TryWaitAllHandshake(new Connection(impl), waitAllHandshake, token);
		}

		public static Task<Connection> P2PMatching(string url)
		{
			return P2PMatching(url, null, CancellationToken.None, waitAllHandshake: true);
		}

		public static async Task<Connection> P2PMatching(string url, string stunURL = null, CancellationToken token = default, bool waitAllHandshake = true)
		{
			var impl = await new MatchingPeersTask(url, stunURL, token).Run();
			return await TryWaitAllHandshake(new Connection(impl), waitAllHandshake, token);
		}

		public static Task<Connection> P2PMatching(Func<StunResult, CancellationToken, Task<P2PSettings>> func)
		{
			return P2PMatching(func, null, CancellationToken.None, waitAllHandshake: true);
		}

		public static async Task<Connection> P2PMatching(Func<StunResult, CancellationToken, Task<P2PSettings>> func, string stunURL = null, CancellationToken token = default, bool waitAllHandshake = true)
		{
			var impl = await new MatchingPeersTask(func, stunURL, token).Run();
			return await TryWaitAllHandshake(new Connection(impl), waitAllHandshake, token);
		}

		static async Task<Connection> TryWaitAllHandshake(Connection conn, bool waitAllHandshake, CancellationToken token)
		{
			if (waitAllHandshake)
			{
				try
				{
					await conn.P2P.WaitHandshake(token);
				}
				catch (Exception)
				{
					conn.Dispose();
					throw;
				}
			}
			return conn;
		}

	}

}