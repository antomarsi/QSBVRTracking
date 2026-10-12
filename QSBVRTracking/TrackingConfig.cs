using OWML.Common;
using System;

namespace QSBVRTracking;

public static class TrackingConfig
{
	public const float MinActivationDistance = 1f;
	public const float MaxActivationDistance = 50f;
	public const float MaxHysteresisMargin = 10f;
	public const int MinUpdateRate = 1;
	public const int MaxUpdateRate = 15;

	public static bool EnableDistanceCulling { get; private set; } = true;
	public static float ActivationDistance { get; private set; } = 10f;
	public static float HysteresisMargin { get; private set; } = 2f;
	public static int TrackingUpdateRate { get; private set; } = 10;
	public static bool EnableAdaptiveUpdateRate { get; private set; } = true;
	public static int MinimumUpdateRate { get; private set; } = 5;
	public static bool EnableDebugVisualization { get; private set; }
	public static bool EnableNetworkDiagnostics { get; private set; }
	public static bool DebugSimulateVR { get; private set; }

	public static float DeactivationDistance => ActivationDistance + HysteresisMargin;
	public static float ActivationSqr { get; private set; } = 100f;
	public static float DeactivationSqr { get; private set; } = 144f;
	public static float SendInterval { get; private set; } = 0.1f;
	public static float MinimumSendInterval { get; private set; } = 0.2f;

	public static void Apply(IModConfig config)
	{
		EnableDistanceCulling = Get(config, "EnableDistanceCulling", true);
		ActivationDistance = Clamp(Get(config, "ActivationDistance", 10f), MinActivationDistance, MaxActivationDistance, 10f);
		HysteresisMargin = Clamp(Get(config, "HysteresisMargin", 2f), 0f, MaxHysteresisMargin, 2f);
		TrackingUpdateRate = (int)Math.Round(Clamp(Get(config, "TrackingUpdateRate", 10f), MinUpdateRate, MaxUpdateRate, 10f));
		EnableAdaptiveUpdateRate = Get(config, "EnableAdaptiveUpdateRate", true);
		MinimumUpdateRate = (int)Math.Round(Clamp(Get(config, "MinimumUpdateRate", 5f), MinUpdateRate, TrackingUpdateRate, 5f));
		EnableDebugVisualization = Get(config, "EnableDebugVisualization", false);
		EnableNetworkDiagnostics = Get(config, "EnableNetworkDiagnostics", false);
		DebugSimulateVR = Get(config, "DebugSimulateVR", false);

		ActivationSqr = ActivationDistance * ActivationDistance;
		DeactivationSqr = DeactivationDistance * DeactivationDistance;
		SendInterval = 1f / TrackingUpdateRate;
		MinimumSendInterval = 1f / MinimumUpdateRate;
	}

	// GetSettingsValue logs an error for missing keys, which happens with a config.json older than the default config.
	private static T Get<T>(IModConfig config, string key, T fallback) =>
		config.Settings != null && config.Settings.ContainsKey(key)
			? config.GetSettingsValue<T>(key)
			: fallback;

	private static float Clamp(float value, float min, float max, float fallback)
	{
		if (float.IsNaN(value) || float.IsInfinity(value))
		{
			return Math.Min(Math.Max(fallback, min), max);
		}

		return Math.Min(Math.Max(value, min), max);
	}
}
