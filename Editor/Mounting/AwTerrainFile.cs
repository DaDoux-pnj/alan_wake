using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// cid_terraindata.bin (version 25). The terrain is an irregular quad/triangle mesh (it has real cliffs),
/// not a heightfield.
///
/// Low detail (LD cell, flag 0):
///   u32 25, u8 0, u32 textureCount, textures{ u32 len, path }, u32 materialCount,
///   materials{ u16 color[3], u16 normal[3] }, u32 vertexCount, vertices{ f32 pos[3], s16 normal[3], u16 },
///   u32 polyCount, polys (196 bytes){ s32 vertex[4] (-1 = triangle), u8 corners, u8[3],
///       corner[4]{ u16 uv0[2], u16 uv1[2], u16 uv2[2], u16 atlas[2] }, tangents s16[4][9], f32 sphere[4],
///       u16 neighbour[4], u8 neighbourEdge[4], u16 material, u16 flags, u32, u8[4] },
///   normal maps, blend maps (8 x 8, see below), ...
///
/// High detail (HD cell, flag 1) adds per poly displacement and finer maps for the same polys:
///   u32 25, u8 1, u32 polyCount, grids{ u32 index, s16[16*16], s32 min, max, mean },
///   u32 count, count x 80 bytes, normal maps, blend maps (16 x 16).
///
/// A map set is u32 count, count x { u32 polyIndex, u32 dim, u16[dim*dim] } (row = t, column = s), with
/// X1R5G5B5 samples: R = bits 10-14, G = bits 5-9, B = bits 0-4.
///   normal maps: world space normal (x, y up, z) = rgb * 2 - 1.
///   blend maps:  R and G are the layer 1 / layer 2 thresholds and B a baked brightness (shadowing under
///                houses etc.), consumed like the game's "TerrainMeshMaker" bake:
///                colour = lerp( C2, lerp( C1, C0, t1 ), t2 ) * B, t1 = sat( (C1.a - R) * 32 ), t2 = sat( (C2.a - G) * 32 ).
/// High detail maps put their first and last samples on the poly edges (neighbours share edge samples);
/// low detail maps sample texel centres.
///
/// Displaced point at grid (r, c): bilinear(corners, s = c/15, t = r/15) + bilinear(corner normals) * height / 2048.
/// Quad corner order on the grid: v0 = (0,0), v1 = (0,15), v2 = (15,15), v3 = (15,0).
/// Triangles use v0 = (0,0), v1 = (15,0), v2 = (15,15) with the (0,15) corner collapsed onto v0.
/// Layer UVs are u16 per corner, <see cref="LayerUvPeriod"/> units per texture repeat (wrapping).
/// </summary>
sealed class AwTerrainFile
{
	/// <summary>Layer UV units per texture repeat. Neighbouring polys only ever differ by whole multiples of it.</summary>
	public const int LayerUvPeriod = 2048;

	public struct Poly
	{
		public int V0, V1, V2, V3;     // V3 = -1 for triangles
		public bool IsTriangle;
		public ushort[] CornerUv;      // 4 corners x 8 u16
		public int Material;
	}

	/// <summary>One per poly map set.</summary>
	public sealed class PolyMaps
	{
		public int Dim;
		public ushort[][] Maps;
		public bool CornerAligned;

		/// <summary>Sample position in texels (texel k is centred on k + 0.5) for a poly coordinate in [0, 1].</summary>
		public float Coord( float s ) => CornerAligned ? s * (Dim - 1) + 0.5f : Math.Clamp( s * Dim, 0.5f, Dim - 0.5f );

		/// <summary>Bilinearly filtered normal map sample, Alan Wake space.</summary>
		public Vector3 SampleNormal( int poly, float s, float t )
		{
			var map = poly < Maps.Length ? Maps[poly] : null;
			if ( map is null || map.Length < Dim * Dim )
				return Vector3.Zero;

			float x = Coord( s ) - 0.5f, y = Coord( t ) - 0.5f;
			int x0 = Math.Clamp( (int)MathF.Floor( x ), 0, Dim - 1 ), y0 = Math.Clamp( (int)MathF.Floor( y ), 0, Dim - 1 );
			int x1 = Math.Min( x0 + 1, Dim - 1 ), y1 = Math.Min( y0 + 1, Dim - 1 );
			float fx = Math.Clamp( x - x0, 0, 1 ), fy = Math.Clamp( y - y0, 0, 1 );

			var top = Vector3.Lerp( DecodeNormal( map[y0 * Dim + x0] ), DecodeNormal( map[y0 * Dim + x1] ), fx );
			var bottom = Vector3.Lerp( DecodeNormal( map[y1 * Dim + x0] ), DecodeNormal( map[y1 * Dim + x1] ), fx );
			return Vector3.Lerp( top, bottom, fy ).Normal;
		}

		static Vector3 DecodeNormal( ushort v )
			=> new( ((v >> 10) & 31) / 15.5f - 1f, ((v >> 5) & 31) / 15.5f - 1f, (v & 31) / 15.5f - 1f );
	}

	public string[] Textures = Array.Empty<string>();
	public ushort[][] Materials = Array.Empty<ushort[]>();
	public Vector3[] Positions = Array.Empty<Vector3>();   // metres, Alan Wake space
	public Vector3[] Normals = Array.Empty<Vector3>();     // Alan Wake space
	public Poly[] Polys = Array.Empty<Poly>();
	public PolyMaps NormalMaps;
	public PolyMaps Blend;

	// High detail data (null when only LD exists)
	public short[][] Heights;
	public PolyMaps HdNormalMaps;
	public PolyMaps HdBlend;

	public static AwTerrainFile ReadLowDetail( byte[] b )
	{
		var t = new AwTerrainFile();
		int p = 0;
		uint version = U32( b, ref p );
		if ( version != 25 ) throw new InvalidDataException( $"Unsupported terrain version {version}" );
		if ( b[p++] != 0 ) throw new InvalidDataException( "Expected low detail terrain" );

		int texCount = (int)U32( b, ref p );
		t.Textures = new string[texCount];
		for ( int i = 0; i < texCount; i++ )
		{
			int len = (int)U32( b, ref p );
			t.Textures[i] = AwArchive.NormalizePath( System.Text.Encoding.Latin1.GetString( b, p, len ).TrimEnd( '\0' ) );
			p += len;
		}

		int matCount = (int)U32( b, ref p );
		t.Materials = new ushort[matCount][];
		for ( int i = 0; i < matCount; i++ )
		{
			var m = new ushort[6];
			for ( int k = 0; k < 6; k++ ) m[k] = BitConverter.ToUInt16( b, p + k * 2 );
			t.Materials[i] = m;
			p += 12;
		}

		int vertCount = (int)U32( b, ref p );
		t.Positions = new Vector3[vertCount];
		t.Normals = new Vector3[vertCount];
		for ( int i = 0; i < vertCount; i++ )
		{
			t.Positions[i] = new Vector3( BitConverter.ToSingle( b, p ), BitConverter.ToSingle( b, p + 4 ), BitConverter.ToSingle( b, p + 8 ) );
			t.Normals[i] = new Vector3( BitConverter.ToInt16( b, p + 12 ) / 32767f, BitConverter.ToInt16( b, p + 14 ) / 32767f, BitConverter.ToInt16( b, p + 16 ) / 32767f );
			p += 20;
		}

		int polyCount = (int)U32( b, ref p );
		t.Polys = new Poly[polyCount];
		for ( int i = 0; i < polyCount; i++ )
		{
			int o = p + i * 196;
			var poly = new Poly
			{
				V0 = BitConverter.ToInt32( b, o ),
				V1 = BitConverter.ToInt32( b, o + 4 ),
				V2 = BitConverter.ToInt32( b, o + 8 ),
				V3 = BitConverter.ToInt32( b, o + 12 ),
				CornerUv = new ushort[32],
				Material = BitConverter.ToUInt16( b, o + 20 + 64 + 72 + 16 + 12 ),
			};
			poly.IsTriangle = b[o + 16] == 3 || poly.V3 < 0;
			for ( int k = 0; k < 32; k++ )
				poly.CornerUv[k] = BitConverter.ToUInt16( b, o + 20 + k * 2 );
			t.Polys[i] = poly;
		}
		p += polyCount * 196;

		try
		{
			t.NormalMaps = ReadMaps( b, ref p, polyCount, cornerAligned: false );
			t.Blend = ReadMaps( b, ref p, polyCount, cornerAligned: false );
		}
		catch ( Exception )
		{
			// keep the geometry: the loader falls back to computed normals and the base layer
			t.NormalMaps = null;
			t.Blend = null;
		}
		return t;
	}

	public void ReadHighDetail( byte[] b )
	{
		int p = 0;
		uint version = U32( b, ref p );
		if ( version != 25 ) throw new InvalidDataException( $"Unsupported terrain version {version}" );
		if ( b[p++] != 1 ) throw new InvalidDataException( "Expected high detail terrain" );

		int count = (int)U32( b, ref p );
		if ( count != Polys.Length )
			throw new InvalidDataException( $"HD terrain has {count} polys, LD has {Polys.Length}" );

		Heights = new short[count][];
		for ( int i = 0; i < count; i++ )
		{
			var g = new short[256];
			for ( int k = 0; k < 256; k++ ) g[k] = BitConverter.ToInt16( b, p + 4 + k * 2 );
			Heights[i] = g;
			p += 4 + 512 + 12;
		}

		int extra = (int)U32( b, ref p );
		p += extra * 80;

		try
		{
			HdNormalMaps = ReadMaps( b, ref p, count, cornerAligned: true );
			HdBlend = ReadMaps( b, ref p, count, cornerAligned: true );
		}
		catch ( Exception )
		{
			// fall back to the LD maps
			HdNormalMaps = null;
			HdBlend = null;
		}
	}

	static PolyMaps ReadMaps( byte[] b, ref int p, int expected, bool cornerAligned )
	{
		if ( p + 4 > b.Length ) return null;
		int count = (int)U32( b, ref p );
		if ( count != expected ) throw new InvalidDataException( $"Expected {expected} poly maps, found {count}" );

		var maps = new ushort[count][];
		int dim = 0;
		for ( int i = 0; i < count; i++ )
		{
			int index = (int)U32( b, ref p );
			int d = (int)U32( b, ref p );
			if ( index != i || d <= 0 || d > 64 || (dim != 0 && d != dim) ) throw new InvalidDataException( "Bad poly map" );
			dim = d;
			var m = new ushort[d * d];
			for ( int k = 0; k < m.Length; k++ ) m[k] = BitConverter.ToUInt16( b, p + k * 2 );
			p += d * d * 2;
			maps[i] = m;
		}
		return new PolyMaps { Dim = Math.Max( 1, dim ), Maps = maps, CornerAligned = cornerAligned };
	}

	static uint U32( byte[] b, ref int p ) { uint v = BitConverter.ToUInt32( b, p ); p += 4; return v; }
}
