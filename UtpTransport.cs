using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Services.Relay.Models;
using System.Threading.Tasks;

namespace Utp
{
	/// <summary>
	/// Component that implements Mirror's Transport class, utilizing the Unity Transport Package (UTP).
	/// </summary>
	[DisallowMultipleComponent]
	public class UtpTransport : Transport
	{
		/// <summary>
		/// The scheme used by this transport.
		/// </summary>
		public const string Scheme = "udp";

		[Header("Transport Configuration")]

		/// <summary>
		/// The port at which to connect.
		/// </summary>
		public ushort Port = 7777;

		[Header("Debugging")]

		/// <summary>
		/// The level of logging sensitivity.
		/// </summary>
		public LogLevel LoggerLevel = LogLevel.Info;

		[Header("Timeout in MS")]

		/// <summary>
		/// The timeout for the Utp server, in milliseconds.
		/// </summary>
		public int TimeoutMS = 1000;

		/// <summary>
		/// Whether to use Relay or not.
		/// </summary>
		// Relay toggle
		public bool useRelay;

		/// <summary>
		/// The UTP server object.
		/// </summary>
		private UtpServer server;

		/// <summary>
		/// The UTP client object.
		/// </summary>
		private UtpClient client;

		/// <summary>
		/// The Relay manager.
		/// </summary>
		public IRelayManager RelayManager { get; private set; }

		/// <summary>
		/// Calls when script is being loaded.
		/// </summary>
		private void Awake()
		{
			//Logging delegates
			if (LoggerLevel < LogLevel.Verbose) UtpLog.Verbose = _ => { };
			if (LoggerLevel < LogLevel.Info) UtpLog.Info = _ => { };
			if (LoggerLevel < LogLevel.Warning) UtpLog.Warning = _ => { };
			if (LoggerLevel < LogLevel.Error) UtpLog.Error = _ => { };

			//Instantiate new UTP server
			server = new UtpServer(
				(connectionId) => OnServerConnected.Invoke(connectionId),
				(connectionId, message) => OnServerDataReceived.Invoke(connectionId, message, Channels.Reliable),
				(connectionId) => OnServerDisconnected.Invoke(connectionId),
				TimeoutMS);

			//Instantiate new UTP client
			
			client = new UtpClient(
				() => OnClientConnected.Invoke(), 
				ClientDataReceivedEvent,
				() => OnClientDisconnected.Invoke(),
				TimeoutMS);

			RelayManager = TryGetComponent<IRelayManager>(out var _relayManager) 
				? _relayManager : gameObject.AddComponent<RelayManager>();
		}

		void ClientDataReceivedEvent(ArraySegment<byte> message)
		{
			if (OnClientDataReceived != null)
				OnClientDataReceived.Invoke(message, Channels.Reliable);
			else DefaultEvent(nameof(OnClientDataReceived));
		}
		private void DefaultEvent(string evntName)
		{
			UtpLog.Warning(evntName + " called with no handler");
		}

		private async void SetupDefaultCallbacks()
		{
			await Task.Delay(500);
			OnServerConnected ??= (connId) => DefaultEvent(nameof(OnServerConnected));
			OnServerDisconnected ??= (connId) => DefaultEvent(nameof(OnServerDisconnected));
			OnServerDataReceived ??= (connId, data, channel) => DefaultEvent(nameof(OnServerDataReceived));
			OnClientConnected ??= () => DefaultEvent(nameof(OnClientConnected));
			OnClientDisconnected ??= () => DefaultEvent(nameof(OnClientDisconnected));
			OnClientDisconnected ??= () => DefaultEvent(nameof(OnClientDisconnected));
			OnClientDataReceived ??= (msg, channel) => DefaultEvent(nameof(OnClientDataReceived));
		}

		/// <summary>
		/// Checks to see if UTP is available on this platform. 
		/// </summary>
		/// <returns>If UTP is available on the current platform.</returns>
		public override bool Available()
		{
			return Application.platform != RuntimePlatform.WebGLPlayer;
		}

		/// <summary>
		/// Connects client to a server address.
		/// </summary>
		/// <param name="address">The address to connect to.</param>
		public override void ClientConnect(string address)
		{
			// We entirely ignore the address that is passed when utilizing Relay
			if (useRelay)
			{
				// The data we need to connect is embedded in the relayManager's JoinAllocation
				client.RelayConnect(RelayManager.JoinAllocation);
			}
			else
			{
				//Join normal IP with port
				if (address.Contains(":"))
				{
					string[] hostAndPort = address.Split(':');
					client.Connect(hostAndPort[0], Convert.ToUInt16(hostAndPort[1]));
				}
				else
				{
					// fallback to default port
					client.Connect(address, Port);
				}
			}
		}

		#region Relay methods

		/// <summary>
		/// Configures a new Relay client with a join code.
		/// </summary>
		/// <param name="joinCode">The Relay join code.</param>
		/// <param name="onSuccess">A callback to invoke when the Relay allocation is successfully retrieved from the join code.</param>
		/// <param name="onFailure">A callback to invoke when the Relay allocation is unsuccessfully retrieved from the join code.</param>
		public Task ConfigureClientWithJoinCode(string joinCode)
		{
			useRelay = true;
			return RelayManager.GetAllocationFromJoinCode(joinCode);
		}

		/// <summary>
		/// Gets region ID's from all the Relay regions (Only use if Relay is enabled).
		/// </summary>
		/// <param name="onSuccess">A callback to invoke when the list of regions is successfully retrieved.</param>
		public Task<List<Region>> GetRelayRegions()
		{
			return RelayManager.GetRelayRegions();
		}

		/// <summary>
		/// Allocates a new Relay server. 
		/// </summary>
		/// <param name="maxPlayers">The maximum player count.</param>
		/// <param name="regionId">The region ID.</param>
		/// <param name="onSuccess">A callback to invoke when the Relay server is successfully allocated.</param>
		/// <param name="onFailure">A callback to invoke when the Relay server is unsuccessfully allocated.</param>
		public Task<string> AllocateRelayServer(int maxPlayers, string regionId)
		{
			return RelayManager.AllocateRelayServer(maxPlayers, regionId);
		}

		/// <summary>
		/// Returns the max packet size for any packet going over the network
		/// </summary>
		/// <param name="channelId"></param>
		/// <returns></returns>
		public override int GetMaxPacketSize(int channelId = Channels.Reliable)
		{
			//Check for client activity
			if (client != null && client.IsConnected())
			{
				return client.PayloadCapacity - client.GetMaxHeaderSize(channelId);
			}
			else if (server != null && server.IsActive())
			{
				return server.PayloadCapacity - server.GetMaxHeaderSize(channelId);
			}
			else
			{
				//Fall back on default MTU
				return NetworkParameterConstants.MTU;
			}
		}

		#endregion

		#region Client overrides

		public override bool ClientConnected() => client.IsConnected();
		public override void ClientDisconnect() => client.Disconnect();
		public override void ClientSend(ArraySegment<byte> segment, int channelId) => client.Send(segment, channelId);

		public override void ClientEarlyUpdate()
		{
			if (enabled) client.Tick();
		}

		#endregion

		#region Server overrides

		public override bool ServerActive() => server.IsActive();
		public override void ServerStart()
		{
			server.Start(Port, useRelay, RelayManager.ServerAllocation);
		}

		public override void ServerStop() => server.Stop();
		public override string ServerGetClientAddress(int connectionId) => server.GetClientAddress(connectionId);
		public override void ServerDisconnect(int connectionId) => server.Disconnect(connectionId);
		public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId) => server.Send(connectionId, segment, channelId);

		public override void ServerEarlyUpdate()
		{
			if (enabled) server.Tick();
		}

		public override Uri ServerUri()
		{
			UriBuilder builder = new UriBuilder();
			builder.Scheme = Scheme;
			builder.Port = Port;

			return builder.Uri;
		}

		#endregion

		#region Transport overrides

		public override void Shutdown()
		{
			if (client.IsConnected()) client.Disconnect();
			if (server.IsNetworkDriverInitialized()) server.Stop();
		}

		public override string ToString() => "UTP";

		#endregion
	}
}