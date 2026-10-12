using OWML.Common;
using System.Diagnostics;
using UnityEngine;

namespace QSBVRTracking;

/// <summary>
/// Lives on a remote player's model and poses its head and arms from received tracking.
/// Runs late so it overrides the Animator and QSB's head rotation; when the weight is zero nothing is written,
/// so the model falls back to QSB's normal animation.
/// </summary>
[DefaultExecutionOrder(10000)]
public class RemoteVRAvatar : MonoBehaviour
{
	private const float BlendTime = 0.25f;
	private const float StaleTimeout = 3f;
	private const float SnapDistanceSqr = 1.5f * 1.5f;
	private const float MarkerSize = 0.08f;

	// Head bone axes in body space (X = down, Y = back, Z = right), inferred from QSB's PlayerHeadRotationSync.
	private static readonly Quaternion HeadBoneBasis = Quaternion.LookRotation(Vector3.right, Vector3.back);
	private static readonly Vector3 LeftElbowBend = new(-0.6f, -1f, -0.4f);
	private static readonly Vector3 RightElbowBend = new(0.6f, -1f, -0.4f);

	// Degrees each joint bends when a digit is fully curled.
	private const float FingerJointAngle = 55f;
	private const float ThumbJointAngle = 30f;

	private struct Arm
	{
		public Transform Upper;
		public Transform Lower;
		public Transform Hand;
		public Quaternion HandBasis;
		public bool HasHandBasis;
		public bool IsValid;
		public Transform[] Thumb;
		public Transform[] FirstFinger;
		public Transform[] SecondFinger;
		public Vector3 PalmDirection;
		public Vector3 ThumbCurlDirection;
	}

	private Transform _body;
	private Transform _camera;
	private Transform _head;
	private Arm _leftArm;
	private Arm _rightArm;

	private PosePacket _target;
	private PosePacket _current;
	private bool _hasPose;
	private bool _tracking;
	private bool _hasSequence;
	private byte _lastSequence;
	private float _lastPacketTime;
	private float _lastPoseTime;
	private float _interval = 0.1f;
	private float _weight;

	private Transform[] _markers;

	public bool IsTracking => _tracking;

	public void Init(Transform body, Transform camera, Animator animator, IModConsole console)
	{
		_body = body;
		_camera = camera;
		_head = animator.GetBoneTransform(HumanBodyBones.Head);
		_leftArm = MakeArm(animator, true, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
			HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftThumbProximal);
		_rightArm = MakeArm(animator, false, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
			HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightThumbProximal);

		if (TrackingDiagnostics.Enabled)
		{
			LogHandBones(_leftArm.Hand, console);
			LogHandBones(_rightArm.Hand, console);
		}

		if (!_head || !_leftArm.IsValid || !_rightArm.IsValid)
		{
			console.WriteLine($"Remote player model is missing bones (head:{(bool)_head} left arm:{_leftArm.IsValid} right arm:{_rightArm.IsValid}).", MessageType.Warning);
		}
		else if (!_leftArm.HasHandBasis || !_rightArm.HasHandBasis)
		{
			console.WriteLine("Remote player model has no finger bones. Hand rotation will not be applied.", MessageType.Warning);
		}
	}

	/// <summary>
	/// Prints the hand bone hierarchy, to work out finger bones and hand orientation for models without Humanoid fingers.
	/// </summary>
	private static void LogHandBones(Transform hand, IModConsole console)
	{
		if (!hand)
		{
			return;
		}

		var builder = new System.Text.StringBuilder($"[Diagnostics] Hand bone '{hand.name}' (parent '{hand.parent.name}'):");
		foreach (var bone in hand.GetComponentsInChildren<Transform>(true))
		{
			if (bone == hand)
			{
				continue;
			}

			var position = hand.InverseTransformPoint(bone.position);
			builder.AppendLine();
			builder.Append($"  {bone.parent.name}/{bone.name} at ({position.x:F3}, {position.y:F3}, {position.z:F3})");
		}

		console.WriteLine(builder.ToString(), MessageType.Info);
	}

	private static Arm MakeArm(Animator animator, bool isLeft, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand, HumanBodyBones middle, HumanBodyBones thumb)
	{
		var arm = new Arm
		{
			Upper = animator.GetBoneTransform(upper),
			Lower = animator.GetBoneTransform(lower),
			Hand = animator.GetBoneTransform(hand)
		};
		arm.IsValid = arm.Upper && arm.Lower && arm.Hand;
		if (!arm.IsValid)
		{
			return arm;
		}

		// Controllers point along the fingers with the thumb side up, so build that frame from the hand's own bones.
		var middleBone = animator.GetBoneTransform(middle);
		var thumbBone = animator.GetBoneTransform(thumb);
		Vector3 fingers;
		Vector3 up;
		if (middleBone && thumbBone)
		{
			fingers = arm.Hand.InverseTransformPoint(middleBone.position);
			up = arm.Hand.InverseTransformPoint(thumbBone.position);
		}
		else
		{
			// The player model has two fingers that are not mapped in its Humanoid avatar. The first is on the thumb side.
			var firstFinger = FindChild(arm.Hand, "Finger_01_01");
			var secondFinger = FindChild(arm.Hand, "Finger_02_01");
			if (!firstFinger || !secondFinger)
			{
				return arm;
			}

			var first = arm.Hand.InverseTransformPoint(firstFinger.position);
			var second = arm.Hand.InverseTransformPoint(secondFinger.position);
			fingers = (first + second) * 0.5f;
			up = first - second;
		}

		up = Vector3.ProjectOnPlane(up, fingers);
		if (fingers.sqrMagnitude > 1e-10f && up.sqrMagnitude > 1e-10f)
		{
			arm.HandBasis = Quaternion.LookRotation(fingers, up);
			arm.HasHandBasis = true;

			// With the fingers forward and the thumb side up, a left palm faces right and a right palm faces left.
			var palmSide = isLeft ? Vector3.right : Vector3.left;
			arm.PalmDirection = arm.HandBasis * palmSide;
			arm.ThumbCurlDirection = arm.HandBasis * (palmSide + Vector3.down);
			arm.Thumb = FindDigit(arm.Hand, "Thumb_01");
			arm.FirstFinger = FindDigit(arm.Hand, "Finger_01");
			arm.SecondFinger = FindDigit(arm.Hand, "Finger_02");
		}

		return arm;
	}

	/// <summary>
	/// Returns the four chained joints of a digit, named like "Finger_01_01_Jnt" to "Finger_01_04_Jnt", or null.
	/// </summary>
	private static Transform[] FindDigit(Transform hand, string digitName)
	{
		var joints = new Transform[4];
		var parent = hand;
		for (var i = 0; i < joints.Length; i++)
		{
			joints[i] = FindChild(parent, $"{digitName}_0{i + 1}");
			if (!joints[i])
			{
				return null;
			}

			parent = joints[i];
		}

		return joints;
	}

	private static void Curl(Transform[] joints, Vector3 towards, float degrees)
	{
		if (joints == null || degrees < 0.5f)
		{
			return;
		}

		// One hinge axis for the whole digit, taken before bending, so it cannot flip as the digit folds past the palm.
		var axis = Vector3.Cross(joints[1].position - joints[0].position, towards);
		if (axis.sqrMagnitude < 1e-12f)
		{
			return;
		}

		var bend = Quaternion.AngleAxis(degrees, axis);
		for (var i = 0; i < joints.Length - 1; i++)
		{
			joints[i].rotation = bend * joints[i].rotation;
		}
	}

	private static Transform FindChild(Transform parent, string nameFragment)
	{
		foreach (Transform child in parent)
		{
			if (child.name.Contains(nameFragment))
			{
				return child;
			}
		}

		return null;
	}

	/// <summary>
	/// Returns false for packets older than the last one seen.
	/// </summary>
	public bool AcceptSequence(byte sequence)
	{
		if (_hasSequence && (byte)(sequence - _lastSequence - 1) >= 128)
		{
			return false;
		}

		_hasSequence = true;
		_lastSequence = sequence;
		return true;
	}

	public void SetPose(in PosePacket packet)
	{
		var now = Time.unscaledTime;
		if (_tracking && _hasPose)
		{
			var delta = now - _lastPoseTime;
			if (delta < 1f)
			{
				_interval = Mathf.Clamp(Mathf.Lerp(_interval, delta, 0.2f), 0.05f, 0.25f);
			}
		}

		if (!_hasPose || _weight <= 0f || IsDiscontinuous(packet))
		{
			_current = packet;
		}

		_target = packet;
		_hasPose = true;
		_tracking = true;
		_lastPoseTime = now;
		_lastPacketTime = now;
	}

	public void Keepalive()
	{
		if (_hasPose)
		{
			_tracking = true;
			_lastPacketTime = Time.unscaledTime;
		}
	}

	public void StopTracking()
	{
		_tracking = false;
		_hasSequence = false;
	}

	private bool IsDiscontinuous(in PosePacket packet) =>
		(packet.Head.Position - _current.Head.Position).sqrMagnitude > SnapDistanceSqr ||
		(packet.Left.Position - _current.Left.Position).sqrMagnitude > SnapDistanceSqr ||
		(packet.Right.Position - _current.Right.Position).sqrMagnitude > SnapDistanceSqr;

	private void LateUpdate()
	{
		if (_tracking && Time.unscaledTime - _lastPacketTime > StaleTimeout)
		{
			StopTracking();
			TrackingDiagnostics.OnTimeout();
		}

		_weight = Mathf.MoveTowards(_weight, _tracking ? 1f : 0f, Time.unscaledDeltaTime / BlendTime);
		if (_weight <= 0f || !_body)
		{
			SetMarkersVisible(false);
			return;
		}

		var startTicks = TrackingDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0L;

		var t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / (_interval * 0.6f));
		Smooth(ref _current.Head, _target.Head, t);
		Smooth(ref _current.Left, _target.Left, t);
		Smooth(ref _current.Right, _target.Right, t);
		_current.LeftCurl = Vector3.Lerp(_current.LeftCurl, _target.LeftCurl, t);
		_current.RightCurl = Vector3.Lerp(_current.RightCurl, _target.RightCurl, t);

		var bodyRotation = _body.rotation;
		if (_head)
		{
			_head.rotation = Quaternion.Slerp(_head.rotation, bodyRotation * _current.Head.Rotation * HeadBoneBasis, _weight);
		}

		// QSB syncs the camera relative to its parent, which under NomaiVR is the play area instead of the body.
		// Tools and the flashlight hang from the camera, so put it where the headset really is.
		if (_camera)
		{
			_camera.SetPositionAndRotation(
				Vector3.Lerp(_camera.position, _body.TransformPoint(_current.Head.Position), _weight),
				Quaternion.Slerp(_camera.rotation, bodyRotation * _current.Head.Rotation, _weight));
		}

		ApplyArm(_leftArm, _current.Left, _current.LeftCurl, _body.TransformDirection(LeftElbowBend), bodyRotation);
		ApplyArm(_rightArm, _current.Right, _current.RightCurl, _body.TransformDirection(RightElbowBend), bodyRotation);

		UpdateMarkers();

		if (startTicks != 0L)
		{
			TrackingDiagnostics.OnAvatarUpdate(Stopwatch.GetTimestamp() - startTicks);
		}
	}

	private static void Smooth(ref Pose current, in Pose target, float t)
	{
		current.Position = Vector3.Lerp(current.Position, target.Position, t);
		current.Rotation = Quaternion.Slerp(current.Rotation, target.Rotation, t);
	}

	private void ApplyArm(in Arm arm, in Pose pose, Vector3 curl, Vector3 bendDirection, Quaternion bodyRotation)
	{
		if (!arm.IsValid)
		{
			return;
		}

		TwoBoneIK.Solve(arm.Upper, arm.Lower, arm.Hand, _body.TransformPoint(pose.Position), bendDirection, _weight);
		if (arm.HasHandBasis)
		{
			var handRotation = bodyRotation * pose.Rotation * Quaternion.Inverse(arm.HandBasis);
			arm.Hand.rotation = Quaternion.Slerp(arm.Hand.rotation, handRotation, _weight);

			var palm = arm.Hand.rotation * arm.PalmDirection;
			Curl(arm.Thumb, arm.Hand.rotation * arm.ThumbCurlDirection, curl.x * ThumbJointAngle * _weight);
			Curl(arm.FirstFinger, palm, curl.y * FingerJointAngle * _weight);
			Curl(arm.SecondFinger, palm, curl.z * FingerJointAngle * _weight);
		}
	}

	private void UpdateMarkers()
	{
		if (!TrackingConfig.EnableDebugVisualization)
		{
			SetMarkersVisible(false);
			return;
		}

		if (_markers == null)
		{
			_markers = new[] { MakeMarker("Head"), MakeMarker("LeftHand"), MakeMarker("RightHand") };
		}

		SetMarkersVisible(true);
		SetMarker(_markers[0], _current.Head);
		SetMarker(_markers[1], _current.Left);
		SetMarker(_markers[2], _current.Right);
	}

	private Transform MakeMarker(string markerName)
	{
		var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
		marker.name = $"QSBVRTracking_{markerName}Marker";
		Destroy(marker.GetComponent<Collider>());
		marker.transform.SetParent(_body, false);
		marker.transform.localScale = Vector3.one * MarkerSize;
		return marker.transform;
	}

	private static void SetMarker(Transform marker, in Pose pose)
	{
		marker.localPosition = pose.Position;
		marker.localRotation = pose.Rotation;
	}

	private void SetMarkersVisible(bool visible)
	{
		if (_markers == null || _markers[0].gameObject.activeSelf == visible)
		{
			return;
		}

		foreach (var marker in _markers)
		{
			marker.gameObject.SetActive(visible);
		}
	}

	private void OnDestroy()
	{
		if (_markers == null)
		{
			return;
		}

		foreach (var marker in _markers)
		{
			if (marker)
			{
				Destroy(marker.gameObject);
			}
		}
	}
}
