using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// grass/grass.bingrs: the grass clumps the game grows on its terrain.
///   u32 3, u32 clumpCount, u32 totalVertices, u32 totalIndices,
///   u32 paramCount, params{ u32 nameLength, name, u32 type (7 = string), u32 length, value } (g_sColorMap = the atlas),
///   clumpCount x { u32 vertexCount, u32 indexCount,
///                  vertices{ f32 position[3] (metres, Y up), u8 u, u8 v, u8 windWeight, u8 }, u16 indices[indexCount] }.
/// Clump UVs are bytes over the whole atlas (a 4 x 4 grid of grass sprites).
///
/// Where clumps grow is per terrain poly, in the high detail terrain data (see <see cref="AwTerrainFile.GrassBlocks"/>):
/// a 16 x 16 grid of 2 bit codes naming the terrain layer under each grid cell, and for each layer two (clump, density)
/// slots. The game's renderer (TessellatorThread::growGrass) scatters density / 16 clumps per square metre of each grid
/// cell at random spots and yaw, standing on the terrain along its normal, within about 20 m of the camera.
/// </summary>
sealed class AwGrassFile
{
	public sealed class Clump
	{
		public Vector3[] Positions;   // s&box space, inches
		public Vector2[] Uvs;
		public float[] Wind;          // 0 at the roots, 1 at the tips
		public int[] Indices;         // s&box winding
	}

	public string Atlas;
	public readonly List<Clump> Clumps = new();

	public static AwGrassFile Parse( byte[] b )
	{
		var file = new AwGrassFile();
		int p = 0;
		U32( b, ref p ); // version 3
		int clumpCount = (int)U32( b, ref p );
		p += 8; // total vertices, total indices

		int paramCount = (int)U32( b, ref p );
		for ( int i = 0; i < paramCount; i++ )
		{
			int nameLength = (int)U32( b, ref p );
			var name = System.Text.Encoding.Latin1.GetString( b, p, nameLength ).TrimEnd( '\0' );
			p += nameLength;
			uint type = U32( b, ref p );
			if ( type != 7 )
				throw new InvalidDataException( $"Unsupported grass parameter type {type}" );
			int length = (int)U32( b, ref p );
			var value = System.Text.Encoding.Latin1.GetString( b, p, length ).TrimEnd( '\0' );
			p += length;
			if ( name == "g_sColorMap" )
				file.Atlas = AwArchive.NormalizePath( value );
		}

		for ( int c = 0; c < clumpCount && p + 8 <= b.Length; c++ )
		{
			int vertexCount = (int)U32( b, ref p );
			int indexCount = (int)U32( b, ref p );
			if ( p + vertexCount * 16 + indexCount * 2 > b.Length )
				throw new InvalidDataException( $"Grass clump {c} runs past the end of the file" );

			var clump = new Clump { Positions = new Vector3[vertexCount], Uvs = new Vector2[vertexCount], Wind = new float[vertexCount], Indices = new int[indexCount - indexCount % 3] };
			for ( int v = 0; v < vertexCount; v++ )
			{
				int o = p + v * 16;
				clump.Positions[v] = AwSpace.Position( BitConverter.ToSingle( b, o ), BitConverter.ToSingle( b, o + 4 ), BitConverter.ToSingle( b, o + 8 ) );
				clump.Uvs[v] = new Vector2( b[o + 12] / 255f, b[o + 13] / 255f );
				clump.Wind[v] = b[o + 14] / 255f;
			}
			p += vertexCount * 16;

			// Reverse the winding: the Y/Z swap mirrors space.
			for ( int k = 0; k + 2 < clump.Indices.Length; k += 3 )
			{
				clump.Indices[k] = BitConverter.ToUInt16( b, p + k * 2 );
				clump.Indices[k + 1] = BitConverter.ToUInt16( b, p + (k + 2) * 2 );
				clump.Indices[k + 2] = BitConverter.ToUInt16( b, p + (k + 1) * 2 );
			}
			p += indexCount * 2;

			file.Clumps.Add( clump );
		}

		return file;
	}

	static uint U32( byte[] b, ref int p ) { uint v = BitConverter.ToUInt32( b, p ); p += 4; return v; }
}
