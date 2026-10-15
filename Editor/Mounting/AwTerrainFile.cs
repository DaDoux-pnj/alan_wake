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
///   u32 count, blend maps{ u32 index, u32 dim (8), u16[dim*dim] }, ...
///
/// High detail (HD cell, flag 1) adds per poly displacement and finer blend maps for the same polys:
///   u32 25, u8 1, u32 polyCount, grids{ u32 index, s16[16*16], s32 min, max, mean },
///   u32 count, count x 80 bytes, u32 count, blend maps{ u32 index, u32 dim (16), u16[dim*dim] }, ...
///
/// Displaced point at grid (r, c): bilinear(corners, s = c/15, t = r/15) + bilinear(corner normals) * height / 2048.
/// Quad corner order on the grid: v0 = (0,0), v1 = (0,15), v2 = (15,15), v3 = (15,0).
/// Triangles use v0 = (0,0), v1 = (15,0), v2 = (15,15) with the (0,15) corner collapsed onto v0.
///
/// Blend samples are X1R5G5B5: two height-blend thresholds and a brightness term, consumed by the
/// "TerrainMeshMaker" logic: colour = lerp( C2, lerp( C1, C0, t1 ), t2 ) * brightness,
/// t1 = sat( (C1.a - x) * 32 ), t2 = sat( (C2.a - y) * 32 ).
/// Layer UVs are u16 per corner (wrapping), scaled by 1/1024.
/// </summary>
sealed class AwTerrainFile
{
	public struct Poly
	{
		public int V0, V1, V2, V3;     // V3 = -1 for triangles
		public bool IsTriangle;
		public ushort[] CornerUv;      // 4 corners x 8 u16
		public int Material;
	}

	public string[] Textures = Array.Empty<string>();
	public ushort[][] Materials = Array.Empty<ushort[]>();
	public Vector3[] Positions = Array.Empty<Vector3>();   // metres, Alan Wake space
	public Vector3[] Normals = Array.Empty<Vector3>();     // Alan Wake space
	public Poly[] Polys = Array.Empty<Poly>();
	public int BlendDim;
	public ushort[][] Blend = Array.Empty<ushort[]>();     // per poly, BlendDim x BlendDim

	// High detail data (null when only LD exists)
	public short[][] Heights;
	public int HdBlendDim;
	public ushort[][] HdBlend;

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

		(t.BlendDim, t.Blend) = ReadMaps( b, ref p, polyCount );
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
			(HdBlendDim, HdBlend) = ReadMaps( b, ref p, count );
		}
		catch ( Exception )
		{
			HdBlend = null; // fall back to the LD blend maps
		}
	}

	static (int Dim, ushort[][] Maps) ReadMaps( byte[] b, ref int p, int expected )
	{
		if ( p + 4 > b.Length ) return (0, Array.Empty<ushort[]>());
		int count = (int)U32( b, ref p );
		if ( count != expected ) throw new InvalidDataException( $"Expected {expected} blend maps, found {count}" );

		var maps = new ushort[count][];
		int dim = 0;
		for ( int i = 0; i < count; i++ )
		{
			int index = (int)U32( b, ref p );
			int d = (int)U32( b, ref p );
			if ( index != i || d <= 0 || d > 64 ) throw new InvalidDataException( "Bad blend map" );
			dim = d;
			var m = new ushort[d * d];
			for ( int k = 0; k < m.Length; k++ ) m[k] = BitConverter.ToUInt16( b, p + k * 2 );
			p += d * d * 2;
			maps[i] = m;
		}
		return (dim, maps);
	}

	static uint U32( byte[] b, ref int p ) { uint v = BitConverter.ToUInt32( b, p ); p += 4; return v; }
}
