using System.Collections.Generic;
using UnityEngine;

namespace QSBVRTracking;

/// <summary>
/// Decides, on the sending side, which players are close enough to receive tracking.
/// </summary>
public class ProximityTracker
{
	private readonly IQSBAPI _qsb;
	private readonly Dictionary<uint, GameObject> _bodies = new();

	/// <summary>
	/// Players currently receiving tracking.
	/// </summary>
	public readonly List<uint> Active = new();
	/// <summary>
	/// Players that stopped receiving tracking in the last <see cref="Evaluate"/>.
	/// </summary>
	public readonly List<uint> Removed = new();
	/// <summary>
	/// Whether a player started receiving tracking in the last <see cref="Evaluate"/>.
	/// </summary>
	public bool Added { get; private set; }

	public ProximityTracker(IQSBAPI qsb) => _qsb = qsb;

	public void Evaluate(List<uint> players, HashSet<uint> capable, uint localId)
	{
		Removed.Clear();
		Added = false;

		var localBody = Locator.GetPlayerTransform();
		var canSend = localBody && !_qsb.GetPlayerDead(localId);
		var localPosition = canSend ? localBody.position : Vector3.zero;

		foreach (var playerId in players)
		{
			if (playerId == localId)
			{
				continue;
			}

			var wasActive = Active.Contains(playerId);
			var isActive = canSend && capable.Contains(playerId) && IsInRange(playerId, localPosition, wasActive);
			if (isActive && !wasActive)
			{
				Active.Add(playerId);
				Added = true;
			}
			else if (!isActive && wasActive)
			{
				Active.Remove(playerId);
				Removed.Add(playerId);
			}
		}
	}

	private bool IsInRange(uint playerId, Vector3 localPosition, bool wasActive)
	{
		if (!_qsb.GetPlayerReady(playerId) || _qsb.GetPlayerDead(playerId))
		{
			return false;
		}

		_bodies.TryGetValue(playerId, out var body);
		if (!body)
		{
			body = _qsb.GetPlayerBody(playerId);
			_bodies[playerId] = body;
		}

		if (!body || !body.activeInHierarchy)
		{
			return false;
		}

		if (!TrackingConfig.EnableDistanceCulling)
		{
			return true;
		}

		// Both bodies live in the same world space on this client, so this holds on moving planets and ships.
		var sqrDistance = (body.transform.position - localPosition).sqrMagnitude;
		return sqrDistance <= (wasActive ? TrackingConfig.DeactivationSqr : TrackingConfig.ActivationSqr);
	}

	public void Forget(uint playerId)
	{
		Active.Remove(playerId);
		_bodies.Remove(playerId);
	}

	public void Reset()
	{
		Active.Clear();
		Removed.Clear();
		_bodies.Clear();
		Added = false;
	}
}
