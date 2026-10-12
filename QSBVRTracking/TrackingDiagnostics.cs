using OWML.Common;
using System.Diagnostics;

namespace QSBVRTracking;

/// <summary>
/// Counters printed every few seconds while network diagnostics are enabled. Byte counts are tracking payload only,
/// without the QSB message wrapper or transport overhead.
/// </summary>
public static class TrackingDiagnostics
{
	private const float ReportInterval = 5f;

	private static int _sentMessages;
	private static int _sentBytes;
	private static int _receivedMessages;
	private static int _receivedBytes;
	private static int _rejectedMessages;
	private static int _timeouts;
	private static long _avatarTicks;
	private static int _avatarFrames;
	private static float _windowStart;

	public static bool Enabled => TrackingConfig.EnableNetworkDiagnostics;

	public static void OnSent(int bytes)
	{
		_sentMessages++;
		_sentBytes += bytes;
	}

	public static void OnReceived(int bytes)
	{
		_receivedMessages++;
		_receivedBytes += bytes;
	}

	public static void OnRejected() => _rejectedMessages++;

	public static void OnTimeout() => _timeouts++;

	public static void OnAvatarUpdate(long ticks)
	{
		_avatarTicks += ticks;
		_avatarFrames++;
	}

	public static void Update(IModConsole console, float now, int recipients, int trackedPlayers)
	{
		var elapsed = now - _windowStart;
		if (elapsed < ReportInterval)
		{
			return;
		}

		if (Enabled)
		{
			var avatarMs = _avatarFrames > 0 ? _avatarTicks * 1000.0 / Stopwatch.Frequency / _avatarFrames : 0.0;
			console.WriteLine(
				$"[Diagnostics] tx {_sentMessages / elapsed:F1} msg/s {_sentBytes / elapsed:F0} B/s to {recipients} recipient(s) | " +
				$"rx {_receivedMessages / elapsed:F1} msg/s {_receivedBytes / elapsed:F0} B/s from {trackedPlayers} VR player(s) | " +
				$"rejected {_rejectedMessages} timeouts {_timeouts} | avatar {avatarMs:F3} ms/frame | " +
				$"rate {TrackingConfig.MinimumUpdateRate}-{TrackingConfig.TrackingUpdateRate} Hz, " +
				$"range {(TrackingConfig.EnableDistanceCulling ? $"{TrackingConfig.ActivationDistance:F1}-{TrackingConfig.DeactivationDistance:F1} m" : "unlimited")}",
				MessageType.Info);
		}

		_sentMessages = 0;
		_sentBytes = 0;
		_receivedMessages = 0;
		_receivedBytes = 0;
		_rejectedMessages = 0;
		_timeouts = 0;
		_avatarTicks = 0;
		_avatarFrames = 0;
		_windowStart = now;
	}
}
