using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// Foliage type (.binfol): u32 19, u32 meshSize, an embedded .binmsh (FoliagePRT materials, vertices with a
/// packed lighting normal + wind data), then three billboard texture paths, 17 floats:
///   [0] billboard width, [1] billboard height (metres), [2..5] impostor rect in the billboard atlas (u, v, w, h),
///   [6..7] mesh to billboard fade (metres), [8..9] visibility fade out (metres), ..., [15] trunk bend factor,
///   [16] crown radius,
/// then u8 "branches follow the copy's wind phase", f32 trunk pivot height (metres), f32[3] (player push, unused).
/// Material flags of the FoliagePRT materials: 1 = large foliage wind table, 2 = leaves flutter.
/// </summary>
sealed class AwFoliageFile
{
	public AwMeshFile Mesh;
	public string BillboardAtlas;
	public float BillboardWidth;
	public float BillboardHeight;
	public Vector4 AtlasRect;
	public float MeshFadeEnd;
	public float VisibleDistance;
	public float WindTrunkBend;
	public bool WindInstancePhase;
	public float WindPivot;

	public static AwFoliageFile Parse( byte[] b )
	{
		uint version = BitConverter.ToUInt32( b, 0 );
		if ( version != 19 )
			throw new InvalidDataException( $"Unsupported binfol version {version}" );

		int meshSize = BitConverter.ToInt32( b, 4 );
		var file = new AwFoliageFile { Mesh = AwMeshFile.Parse( b.AsSpan( 8, meshSize ).ToArray() ) };

		int p = 8 + meshSize;
		var strings = new string[3];
		for ( int i = 0; i < 3; i++ )
		{
			int len = BitConverter.ToInt32( b, p );
			strings[i] = System.Text.Encoding.Latin1.GetString( b, p + 4, len ).TrimEnd( '\0' );
			p += 4 + len;
		}
		file.BillboardAtlas = strings[0];

		float F( int index ) => p + index * 4 + 4 <= b.Length ? BitConverter.ToSingle( b, p + index * 4 ) : 0f;
		file.BillboardWidth = F( 0 );
		file.BillboardHeight = F( 1 );
		file.AtlasRect = new Vector4( F( 2 ), F( 3 ), F( 4 ), F( 5 ) );
		file.MeshFadeEnd = F( 7 );
		file.VisibleDistance = F( 9 );
		file.WindTrunkBend = F( 15 );

		int q = p + 17 * 4;
		if ( q + 5 <= b.Length )
		{
			file.WindInstancePhase = b[q] != 0;
			file.WindPivot = BitConverter.ToSingle( b, q + 1 );
		}

		return file;
	}
}

/// <summary>cid_foliagedata.bin (version 9): placed foliage of one cell.</summary>
static class AwFoliagePlacement
{
	/// <summary>One copy: position in metres (Alan Wake space), yaw 0..255, index into the type list.</summary>
	public readonly record struct Instance( Vector3 Position, byte Yaw, byte Type );

	/// <summary>u32 version, u32 typeCount, types{ u32 len, "data\foliage\x.binfol" }, u32 count, count x 20 bytes.</summary>
	public static (List<string> Types, List<Instance> Instances) Read( byte[] b )
	{
		var types = new List<string>();
		var instances = new List<Instance>();
		if ( b is null || b.Length < 12 )
			return (types, instances);

		int p = 4;
		int typeCount = BitConverter.ToInt32( b, p ); p += 4;
		for ( int i = 0; i < typeCount; i++ )
		{
			int len = BitConverter.ToInt32( b, p );
			types.Add( AwArchive.NormalizePath( System.Text.Encoding.Latin1.GetString( b, p + 4, len ).TrimEnd( '\0' ) ) );
			p += 4 + len;
		}

		int count = BitConverter.ToInt32( b, p ); p += 4;
		if ( p + count * 20 > b.Length )
			throw new InvalidDataException( $"cid_foliagedata: {count} instances don't fit" );

		for ( int i = 0; i < count; i++ )
		{
			int o = p + i * 20;
			var pos = new Vector3( BitConverter.ToSingle( b, o ), BitConverter.ToSingle( b, o + 4 ), BitConverter.ToSingle( b, o + 8 ) );
			instances.Add( new Instance( pos, b[o + 12], b[o + 13] ) );
		}

		return (types, instances);
	}
}
