using UnityEngine;

namespace QSBVRTracking;

public struct Pose
{
	public Vector3 Position;
	public Quaternion Rotation;
}

/// <summary>
/// The three tracked devices, relative to the player body, and how far each hand's digits are curled.
/// </summary>
public struct PosePacket
{
	public Pose Head;
	public Pose Left;
	public Pose Right;
	/// <summary>
	/// 0 (open) to 1 (closed) for the thumb (x), first finger (y) and second finger (z).
	/// </summary>
	public Vector3 LeftCurl;
	/// <inheritdoc cref="LeftCurl"/>
	public Vector3 RightCurl;
}

/// <summary>
/// Wire format 2: [format &lt;&lt; 4 | flags] [sequence] then, unless a flag is set, 3 poses of 7 floats each and 6 curl bytes.
/// </summary>
public static unsafe class PoseCodec
{
	public const byte Format = 2;
	public const byte FlagStop = 1;
	public const byte FlagKeepalive = 2;

	public const int HeaderSize = 2;
	public const int PoseSize = 7 * sizeof(float);
	public const int CurlSize = 6;
	public const int FullSize = HeaderSize + 3 * PoseSize + CurlSize;

	/// <summary>
	/// Received positions further than this from the player body are rejected.
	/// </summary>
	public const float MaxOffset = 3f;

	public static void WriteHeader(byte[] buffer, byte flags, byte sequence)
	{
		buffer[0] = (byte)((Format << 4) | (flags & 0xF));
		buffer[1] = sequence;
	}

	public static void WritePoses(byte[] buffer, in PosePacket packet)
	{
		var offset = HeaderSize;
		Write(buffer, ref offset, packet.Head);
		Write(buffer, ref offset, packet.Left);
		Write(buffer, ref offset, packet.Right);
		Write(buffer, ref offset, packet.LeftCurl);
		Write(buffer, ref offset, packet.RightCurl);
	}

	public static bool TryReadHeader(byte[] data, out byte flags, out byte sequence)
	{
		flags = 0;
		sequence = 0;
		if (data == null || data.Length < HeaderSize || data[0] >> 4 != Format)
		{
			return false;
		}

		flags = (byte)(data[0] & 0xF);
		sequence = data[1];
		return true;
	}

	public static bool TryReadPoses(byte[] data, out PosePacket packet)
	{
		packet = default;
		if (data == null || data.Length != FullSize)
		{
			return false;
		}

		var offset = HeaderSize;
		if (!TryRead(data, ref offset, out packet.Head)
			|| !TryRead(data, ref offset, out packet.Left)
			|| !TryRead(data, ref offset, out packet.Right))
		{
			return false;
		}

		packet.LeftCurl = ReadCurl(data, ref offset);
		packet.RightCurl = ReadCurl(data, ref offset);
		return true;
	}

	private static void Write(byte[] buffer, ref int offset, Vector3 curl)
	{
		buffer[offset++] = ToByte(curl.x);
		buffer[offset++] = ToByte(curl.y);
		buffer[offset++] = ToByte(curl.z);
	}

	private static byte ToByte(float value) => (byte)(Mathf.Clamp01(value) * 255f + 0.5f);

	private static Vector3 ReadCurl(byte[] data, ref int offset)
	{
		var curl = new Vector3(data[offset], data[offset + 1], data[offset + 2]) / 255f;
		offset += 3;
		return curl;
	}

	private static void Write(byte[] buffer, ref int offset, in Pose pose)
	{
		fixed (byte* ptr = &buffer[offset])
		{
			var floats = (float*)ptr;
			floats[0] = pose.Position.x;
			floats[1] = pose.Position.y;
			floats[2] = pose.Position.z;
			floats[3] = pose.Rotation.x;
			floats[4] = pose.Rotation.y;
			floats[5] = pose.Rotation.z;
			floats[6] = pose.Rotation.w;
		}

		offset += PoseSize;
	}

	private static bool TryRead(byte[] data, ref int offset, out Pose pose)
	{
		pose = default;
		fixed (byte* ptr = &data[offset])
		{
			var floats = (float*)ptr;
			for (var i = 0; i < 7; i++)
			{
				if (float.IsNaN(floats[i]) || float.IsInfinity(floats[i]))
				{
					return false;
				}
			}

			for (var i = 0; i < 3; i++)
			{
				if (floats[i] < -MaxOffset || floats[i] > MaxOffset)
				{
					return false;
				}
			}

			var x = floats[3];
			var y = floats[4];
			var z = floats[5];
			var w = floats[6];
			var sqrMagnitude = x * x + y * y + z * z + w * w;
			if (sqrMagnitude < 0.5f || sqrMagnitude > 1.5f)
			{
				return false;
			}

			var scale = 1f / Mathf.Sqrt(sqrMagnitude);
			pose.Position = new Vector3(floats[0], floats[1], floats[2]);
			pose.Rotation = new Quaternion(x * scale, y * scale, z * scale, w * scale);
		}

		offset += PoseSize;
		return true;
	}
}
