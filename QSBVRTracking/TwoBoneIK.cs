using UnityEngine;

namespace QSBVRTracking;

public static class TwoBoneIK
{
	/// <summary>
	/// Rotates <paramref name="upper"/> and <paramref name="lower"/> so that <paramref name="end"/> reaches <paramref name="target"/>,
	/// bending the middle joint towards <paramref name="bendDirection"/>.
	/// </summary>
	public static void Solve(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 bendDirection, float weight)
	{
		var root = upper.position;
		var upperLength = Vector3.Distance(root, lower.position);
		var lowerLength = Vector3.Distance(lower.position, end.position);
		if (upperLength < 1e-4f || lowerLength < 1e-4f)
		{
			return;
		}

		var toTarget = target - root;
		var distance = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upperLength - lowerLength) + 1e-3f, upperLength + lowerLength - 1e-3f);
		if (toTarget.sqrMagnitude < 1e-8f)
		{
			return;
		}

		var direction = toTarget.normalized;
		var bend = Vector3.ProjectOnPlane(bendDirection, direction);
		if (bend.sqrMagnitude < 1e-6f)
		{
			bend = Vector3.ProjectOnPlane(lower.position - root, direction);
		}

		if (bend.sqrMagnitude < 1e-6f)
		{
			return;
		}

		bend.Normalize();

		// Law of cosines: where the middle joint sits along and off the root-to-target line.
		var along = (upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * distance);
		var off = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
		var middleTarget = root + direction * along + bend * off;
		var endTarget = root + direction * distance;

		var upperRotation = Quaternion.FromToRotation(lower.position - root, middleTarget - root) * upper.rotation;
		upper.rotation = Quaternion.Slerp(upper.rotation, upperRotation, weight);

		var lowerRotation = Quaternion.FromToRotation(end.position - lower.position, endTarget - lower.position) * lower.rotation;
		lower.rotation = Quaternion.Slerp(lower.rotation, lowerRotation, weight);
	}
}
