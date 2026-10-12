using OWML.Common;
using System;
using System.Reflection;
using UnityEngine;

namespace QSBVRTracking;

/// <summary>
/// Reads the headset and controller poses from NomaiVR, relative to the local player body.
/// NomaiVR is only reached through reflection, so it is never required to be installed.
/// </summary>
public class VRPoseProvider
{
	private const string NomaiVRUniqueName = "Raicuparta.NomaiVR";
	private const string HandsTypeName = "NomaiVR.Hands.HandsController+Behaviour";
	private const float ResolveInterval = 1f;

	private readonly IModConsole _console;
	private readonly bool _hasNomaiVR;

	private FieldInfo _leftHandField;
	private FieldInfo _rightHandField;
	private bool _reflectionFailed;

	private Transform _body;
	private Transform _head;
	private Transform _leftHand;
	private Transform _rightHand;
	private float _nextResolveTime;

	/// <summary>
	/// A digit's curl is the angle between its first segment (A to B) and its last (C to D).
	/// </summary>
	private struct Digit
	{
		public Transform A;
		public Transform B;
		public Transform C;
		public Transform D;
		public float OpenAngle;
		public float ClosedAngle;

		public float Curl => A && B && C && D
			? Mathf.InverseLerp(OpenAngle, ClosedAngle, Vector3.Angle(B.position - A.position, D.position - C.position))
			: 0f;
	}

	private Digit[] _leftDigits;
	private Digit[] _rightDigits;

	public VRPoseProvider(IModHelper helper)
	{
		_console = helper.Console;
		_hasNomaiVR = helper.Interaction.ModExists(NomaiVRUniqueName);
	}

	public bool IsActive => _hasNomaiVR || TrackingConfig.DebugSimulateVR;

	/// <summary>
	/// NomaiVR recreates its hands on every scene load.
	/// </summary>
	public void Invalidate()
	{
		_body = null;
		_head = null;
		_leftHand = null;
		_rightHand = null;
		_leftDigits = null;
		_rightDigits = null;
		_nextResolveTime = 0f;
	}

	public bool TryGetPoses(out PosePacket packet)
	{
		packet = default;
		var simulate = !_hasNomaiVR;

		if ((!_body || !_head || (!simulate && (!_leftHand || !_rightHand))) && !Resolve(simulate))
		{
			return false;
		}

		packet.Head = ToBodySpace(_head.position, _head.rotation);
		if (simulate)
		{
			var wave = 0.15f * Mathf.Sin(Time.time * 2f);
			var bodyRotation = _body.rotation;
			packet.Left = ToBodySpace(_head.TransformPoint(new Vector3(-0.25f, -0.35f + wave, 0.35f)), bodyRotation);
			packet.Right = ToBodySpace(_head.TransformPoint(new Vector3(0.25f, -0.35f - wave, 0.35f)), bodyRotation);
			packet.LeftCurl = Vector3.one * (0.5f + 0.5f * Mathf.Sin(Time.time * 3f));
			packet.RightCurl = Vector3.one - packet.LeftCurl;
		}
		else
		{
			packet.Left = ToBodySpace(_leftHand.position, _leftHand.rotation);
			packet.Right = ToBodySpace(_rightHand.position, _rightHand.rotation);
			packet.LeftCurl = GetCurl(_leftDigits);
			packet.RightCurl = GetCurl(_rightDigits);
		}

		return true;
	}

	private static Vector3 GetCurl(Digit[] digits) =>
		digits == null ? Vector3.zero : new Vector3(digits[0].Curl, digits[1].Curl, digits[2].Curl);

	/// <summary>
	/// NomaiVR's hand model has a full SteamVR hand skeleton. The game's player model only has a thumb and two fingers,
	/// which NomaiVR drives from the index and ring fingers.
	/// </summary>
	private Digit[] FindDigits(Transform hand)
	{
		Transform wrist = null;
		foreach (var child in hand.GetComponentsInChildren<Transform>(true))
		{
			if (child.name == "skeletal_hand")
			{
				wrist = child.Find("Root/wrist_r");
				break;
			}
		}

		if (!wrist)
		{
			_console.WriteLine($"Could not find NomaiVR's hand skeleton on {hand.name}. Finger movement will not be sent.", MessageType.Warning);
			return null;
		}

		return new[]
		{
			new Digit
			{
				A = wrist.Find("finger_thumb_0_r"),
				B = wrist.Find("finger_thumb_0_r/finger_thumb_1_r"),
				C = wrist.Find("finger_thumb_0_r/finger_thumb_1_r"),
				D = wrist.Find("finger_thumb_0_r/finger_thumb_1_r/finger_thumb_2_r"),
				OpenAngle = 10f,
				ClosedAngle = 70f
			},
			FindFinger(wrist, "index"),
			FindFinger(wrist, "ring")
		};
	}

	private static Digit FindFinger(Transform wrist, string finger)
	{
		var meta = $"finger_{finger}_meta_r";
		var first = $"{meta}/finger_{finger}_0_r";
		var second = $"{first}/finger_{finger}_1_r";
		return new Digit
		{
			A = wrist.Find(meta),
			B = wrist.Find(first),
			C = wrist.Find(second),
			D = wrist.Find($"{second}/finger_{finger}_2_r"),
			OpenAngle = 20f,
			ClosedAngle = 140f
		};
	}

	private Pose ToBodySpace(Vector3 position, Quaternion rotation) => new()
	{
		Position = _body.InverseTransformPoint(position),
		Rotation = Quaternion.Inverse(_body.rotation) * rotation
	};

	private bool Resolve(bool simulate)
	{
		if (Time.unscaledTime < _nextResolveTime)
		{
			return false;
		}

		_nextResolveTime = Time.unscaledTime + ResolveInterval;

		_body = Locator.GetPlayerTransform();
		var camera = Locator.GetPlayerCamera();
		_head = camera != null ? camera.transform : null;
		if (!_body || !_head)
		{
			return false;
		}

		if (simulate)
		{
			return true;
		}

		ResolveHands();
		if (!_leftHand || !_rightHand)
		{
			return false;
		}

		_leftDigits = FindDigits(_leftHand);
		_rightDigits = FindDigits(_rightHand);
		return true;
	}

	private void ResolveHands()
	{
		if (_leftHandField == null && !_reflectionFailed)
		{
			FindHandFields();
		}

		if (_leftHandField != null)
		{
			_leftHand = _leftHandField.GetValue(null) as Transform;
			_rightHand = _rightHandField.GetValue(null) as Transform;
			return;
		}

		var leftObject = GameObject.Find("VrLeftHand");
		var rightObject = GameObject.Find("VrRightHand");
		_leftHand = leftObject != null ? leftObject.transform : null;
		_rightHand = rightObject != null ? rightObject.transform : null;
	}

	private void FindHandFields()
	{
		try
		{
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.GetName().Name != "NomaiVR")
				{
					continue;
				}

				var type = assembly.GetType(HandsTypeName);
				var left = type?.GetField("LeftHand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				var right = type?.GetField("RightHand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (left != null && right != null)
				{
					_leftHandField = left;
					_rightHandField = right;
					return;
				}
			}
		}
		catch (Exception ex)
		{
			_console.WriteLine($"Exception while looking for NomaiVR hands: {ex}", MessageType.Error);
		}

		_reflectionFailed = true;
		_console.WriteLine("Could not find NomaiVR hand fields. Falling back to searching for the hand objects by name.", MessageType.Warning);
	}
}
