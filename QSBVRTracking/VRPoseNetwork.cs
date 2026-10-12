using OWML.Common;
using System.Collections.Generic;
using UnityEngine;

namespace QSBVRTracking;

/// <summary>
/// Sends the local VR poses to nearby players and applies the poses received from other VR players,
/// using QSB's public addon messaging.
/// </summary>
public class VRPoseNetwork
{
	public const int ProtocolVersion = 1;

	private const string ProtocolKey = "QSBVRTracking.Protocol";
	private const string HelloMessage = "QSBVRT.Hello";
	private const string PoseMessage = "QSBVRT.Pose";
	private const string RemoteModelName = "REMOTE_Traveller_HEA_Player_v2";

	private const float PollInterval = 1f;
	private const float ProximityInterval = 0.25f;
	private const float KeepaliveInterval = 1f;

	// Pose change since the last sent packet, below which nothing is sent.
	private const float IdlePositionSqr = 0.01f * 0.01f;
	private const float IdleAngle = 2f;
	// Pose change since the last sent packet, above which the full update rate is used.
	private const float FastPositionSqr = 0.05f * 0.05f;
	private const float FastAngle = 10f;
	private const float IdleCurlSqr = 0.08f * 0.08f;
	private const float FastCurlSqr = 0.4f * 0.4f;

	private readonly IQSBAPI _qsb;
	private readonly IModConsole _console;
	private readonly VRPoseProvider _poses;
	private readonly ProximityTracker _proximity;

	private readonly List<uint> _players = new();
	private readonly HashSet<uint> _capable = new();
	private readonly Dictionary<uint, RemoteVRAvatar> _avatars = new();
	private uint _localId = uint.MaxValue;
	private bool _announced;
	private float _nextPollTime;

	private readonly byte[] _poseBuffer = new byte[PoseCodec.FullSize];
	private readonly byte[] _headerBuffer = new byte[PoseCodec.HeaderSize];
	private PosePacket _lastSent;
	private byte _sequence;
	private bool _forceFull;
	private float _nextProximityTime;
	private float _nextSendTime;
	private float _lastSendTime;
	private bool _warnedMissingModel;

	public VRPoseNetwork(IQSBAPI qsb, IModHelper helper)
	{
		_qsb = qsb;
		_console = helper.Console;
		_poses = new VRPoseProvider(helper);
		_proximity = new ProximityTracker(qsb);

		_qsb.OnPlayerJoin().AddListener(OnPlayerJoin);
		_qsb.OnPlayerLeave().AddListener(OnPlayerLeave);
		_qsb.RegisterHandler<int>(HelloMessage, OnHello);
		_qsb.RegisterHandler<byte[]>(PoseMessage, OnPose);
	}

	private void OnPlayerJoin(uint playerId)
	{
		if (!_players.Contains(playerId))
		{
			_players.Add(playerId);
		}
	}

	private void OnPlayerLeave(uint playerId)
	{
		_players.Remove(playerId);
		_capable.Remove(playerId);
		_proximity.Forget(playerId);
		RemoveAvatar(playerId);

		if (playerId == _localId)
		{
			_players.Clear();
			_capable.Clear();
			_proximity.Reset();
			foreach (var avatar in _avatars.Values)
			{
				if (avatar)
				{
					Object.Destroy(avatar);
				}
			}

			_avatars.Clear();
			_localId = uint.MaxValue;
			_announced = false;
		}
	}

	/// <summary>
	/// Player bodies and NomaiVR's hands are recreated on scene load.
	/// </summary>
	public void OnSceneChanged()
	{
		_poses.Invalidate();
		_proximity.Reset();
		_forceFull = true;
	}

	public void Update()
	{
		if (_players.Count == 0)
		{
			return;
		}

		var now = Time.unscaledTime;
		if (now >= _nextPollTime)
		{
			_nextPollTime = now + PollInterval;
			Poll();
		}

		if (!_announced)
		{
			return;
		}

		UpdateSender(now);
		TrackingDiagnostics.Update(_console, now, _proximity.Active.Count, CountTrackedPlayers());
	}

	private void Poll()
	{
		if (!_qsb.GetIsInMultiplayer())
		{
			return;
		}

		if (_localId == uint.MaxValue)
		{
			_localId = _qsb.GetLocalPlayerID();
			if (_localId == uint.MaxValue)
			{
				return;
			}
		}

		// Custom data is relayed to players that may not know us yet, so wait until we are fully in the game.
		if (!_announced)
		{
			if (!_qsb.GetPlayerReady(_localId))
			{
				return;
			}

			_qsb.SetCustomData(_localId, ProtocolKey, ProtocolVersion);
			_announced = true;
			_console.WriteLine($"Announced tracking protocol {ProtocolVersion} as player {_localId}.", MessageType.Info);
		}

		foreach (var playerId in _players)
		{
			if (playerId == _localId || _capable.Contains(playerId) || _qsb.GetCustomData<int>(playerId, ProtocolKey) < 1)
			{
				continue;
			}

			_capable.Add(playerId);
			_qsb.SendMessage(HelloMessage, ProtocolVersion, playerId);
			_console.WriteLine($"Sent hello to player {playerId} ({_qsb.GetPlayerName(playerId)}).", MessageType.Info);
		}
	}

	private void OnHello(uint from, int protocolVersion) =>
		_console.WriteLine($"Received hello from player {from} (protocol {protocolVersion}).", MessageType.Success);

	#region Sending

	private void UpdateSender(float now)
	{
		if (!_poses.IsActive)
		{
			if (_proximity.Active.Count > 0)
			{
				foreach (var playerId in _proximity.Active)
				{
					SendStop(playerId);
				}

				_proximity.Reset();
			}

			return;
		}

		if (now >= _nextProximityTime)
		{
			_nextProximityTime = now + ProximityInterval;
			_proximity.Evaluate(_players, _capable, _localId);
			foreach (var playerId in _proximity.Removed)
			{
				SendStop(playerId);
			}

			if (_proximity.Added)
			{
				_forceFull = true;
			}
		}

		if (_proximity.Active.Count == 0 || now < _nextSendTime)
		{
			return;
		}

		_nextSendTime = now + TrackingConfig.SendInterval;
		if (!_poses.TryGetPoses(out var packet))
		{
			return;
		}

		var sinceLastSend = now - _lastSendTime;
		if (!_forceFull)
		{
			if (!HasChanged(packet, IdlePositionSqr, IdleAngle, IdleCurlSqr))
			{
				if (sinceLastSend >= KeepaliveInterval)
				{
					PoseCodec.WriteHeader(_headerBuffer, PoseCodec.FlagKeepalive, _sequence++);
					SendToActive(_headerBuffer);
					_lastSendTime = now;
				}

				return;
			}

			if (TrackingConfig.EnableAdaptiveUpdateRate
				&& sinceLastSend < TrackingConfig.MinimumSendInterval
				&& !HasChanged(packet, FastPositionSqr, FastAngle, FastCurlSqr))
			{
				return;
			}
		}

		PoseCodec.WriteHeader(_poseBuffer, 0, _sequence++);
		PoseCodec.WritePoses(_poseBuffer, packet);
		SendToActive(_poseBuffer);
		_lastSent = packet;
		_lastSendTime = now;
		_forceFull = false;
	}

	private bool HasChanged(in PosePacket packet, float positionSqr, float angle, float curlSqr) =>
		HasChanged(packet.Head, _lastSent.Head, positionSqr, angle) ||
		HasChanged(packet.Left, _lastSent.Left, positionSqr, angle) ||
		HasChanged(packet.Right, _lastSent.Right, positionSqr, angle) ||
		(packet.LeftCurl - _lastSent.LeftCurl).sqrMagnitude > curlSqr ||
		(packet.RightCurl - _lastSent.RightCurl).sqrMagnitude > curlSqr;

	private static bool HasChanged(in Pose pose, in Pose previous, float positionSqr, float angle) =>
		(pose.Position - previous.Position).sqrMagnitude > positionSqr ||
		Quaternion.Angle(pose.Rotation, previous.Rotation) > angle;

	private void SendToActive(byte[] data)
	{
		foreach (var playerId in _proximity.Active)
		{
			_qsb.SendMessage(PoseMessage, data, playerId);
			TrackingDiagnostics.OnSent(data.Length);
		}
	}

	private void SendStop(uint playerId)
	{
		PoseCodec.WriteHeader(_headerBuffer, PoseCodec.FlagStop, _sequence++);
		_qsb.SendMessage(PoseMessage, _headerBuffer, playerId);
		TrackingDiagnostics.OnSent(_headerBuffer.Length);
	}

	#endregion

	#region Receiving

	private void OnPose(uint from, byte[] data)
	{
		if (from == _localId || !_players.Contains(from) || !PoseCodec.TryReadHeader(data, out var flags, out var sequence))
		{
			TrackingDiagnostics.OnRejected();
			return;
		}

		TrackingDiagnostics.OnReceived(data.Length);

		var isPose = flags == 0;
		var avatar = GetAvatar(from, isPose);
		if (!avatar || !avatar.AcceptSequence(sequence))
		{
			return;
		}

		if ((flags & PoseCodec.FlagStop) != 0)
		{
			avatar.StopTracking();
		}
		else if ((flags & PoseCodec.FlagKeepalive) != 0)
		{
			avatar.Keepalive();
		}
		else if (PoseCodec.TryReadPoses(data, out var packet))
		{
			avatar.SetPose(packet);
		}
		else
		{
			TrackingDiagnostics.OnRejected();
		}
	}

	private RemoteVRAvatar GetAvatar(uint playerId, bool create)
	{
		if (_avatars.TryGetValue(playerId, out var avatar) && avatar)
		{
			return avatar;
		}

		if (!create || !_qsb.GetPlayerReady(playerId))
		{
			return null;
		}

		var body = _qsb.GetPlayerBody(playerId);
		var model = body ? body.transform.Find(RemoteModelName) : null;
		var animator = model ? model.GetComponent<Animator>() : null;
		if (!animator)
		{
			if (!_warnedMissingModel)
			{
				_warnedMissingModel = true;
				_console.WriteLine($"Could not find the model of remote player {playerId}. Their VR tracking will not be shown.", MessageType.Warning);
			}

			return null;
		}

		avatar = model.gameObject.AddComponent<RemoteVRAvatar>();
		var camera = _qsb.GetPlayerCamera(playerId);
		avatar.Init(body.transform, camera ? camera.transform : null, animator, _console);
		_avatars[playerId] = avatar;
		return avatar;
	}

	private void RemoveAvatar(uint playerId)
	{
		if (_avatars.TryGetValue(playerId, out var avatar) && avatar)
		{
			Object.Destroy(avatar);
		}

		_avatars.Remove(playerId);
	}

	private int CountTrackedPlayers()
	{
		var count = 0;
		foreach (var avatar in _avatars.Values)
		{
			if (avatar && avatar.IsTracking)
			{
				count++;
			}
		}

		return count;
	}

	#endregion
}
