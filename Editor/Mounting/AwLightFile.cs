using System;
using System.Collections.Generic;

namespace Sandbox.Mounting;

/// <summary>A placed light. Colour is linear RGB, distances in metres, Alan Wake space.</summary>
readonly record struct AwLight( float[] Rotation, Vector3 Position, Vector3 Color, float Intensity, float Radius, bool Shadows, bool Spot, float ConeTangent );

/// <summary>
/// cid_pointlight.bin (version 11, 156 byte records) and cid_spotlight.bin (version 20, 182 byte records).
/// Both start with u32 class, u32 GID, f32[9] rotation, f32[3] position, f32[3] colour, f32 intensity.
///   point: +76 u8 enabled, u8 shadows, +78 f32 radius
///   spot:  +87 f32 range, +91 f32 tan(half cone angle); the light shines along the local +Z axis
/// </summary>
static class AwLightFile
{
	const int PointRecordSize = 156;
	const int SpotRecordSize = 182;

	public static void ReadPointLights( byte[] data, List<AwLight> output )
	{
		foreach ( var o in Records( data, PointRecordSize ) )
		{
			var (rotation, position, color, intensity) = Common( data, o );
			bool shadows = data[o + 77] != 0;
			float radius = BitConverter.ToSingle( data, o + 78 );
			output.Add( new AwLight( rotation, position, color, intensity, radius, shadows, false, 0 ) );
		}
	}

	public static void ReadSpotLights( byte[] data, List<AwLight> output )
	{
		foreach ( var o in Records( data, SpotRecordSize ) )
		{
			var (rotation, position, color, intensity) = Common( data, o );
			float range = BitConverter.ToSingle( data, o + 87 );
			float coneTangent = BitConverter.ToSingle( data, o + 91 );
			output.Add( new AwLight( rotation, position, color, intensity, range, true, true, coneTangent ) );
		}
	}

	static IEnumerable<int> Records( byte[] data, int recordSize )
	{
		if ( data is null || data.Length < 16 )
			yield break;

		int count = BitConverter.ToInt32( data, 8 );
		if ( 16 + count * recordSize != data.Length )
			yield break; // unknown layout: skip rather than misread

		for ( int i = 0; i < count; i++ )
			yield return 16 + i * recordSize;
	}

	static (float[] Rotation, Vector3 Position, Vector3 Color, float Intensity) Common( byte[] b, int o )
	{
		var m = new float[9];
		for ( int k = 0; k < 9; k++ )
			m[k] = BitConverter.ToSingle( b, o + 8 + k * 4 );
		var position = new Vector3( BitConverter.ToSingle( b, o + 44 ), BitConverter.ToSingle( b, o + 48 ), BitConverter.ToSingle( b, o + 52 ) );
		var color = new Vector3( BitConverter.ToSingle( b, o + 56 ), BitConverter.ToSingle( b, o + 60 ), BitConverter.ToSingle( b, o + 64 ) );
		float intensity = BitConverter.ToSingle( b, o + 68 );
		return (m, position, color, intensity);
	}
}
