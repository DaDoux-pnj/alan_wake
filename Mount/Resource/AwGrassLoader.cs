using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// One of the game's grass clumps (grass/clump_NN), standing on +Z, lit with an upward normal; vertex colour red is
/// the game's wind weight (0 at the roots, 1 at the tips) for shaders/aw_grass.shader.
/// </summary>
sealed class AwGrassClumpLoader( int index ) : ResourceLoader<AlanWakeMount>
{
	protected override object Load()
	{
		try
		{
			var grass = Host.GetGrass();
			if ( grass is null || index >= grass.Clumps.Count )
				return null;

			var clump = grass.Clumps[index];
			var vertices = new List<AwGrassVertex>( clump.Positions.Length );
			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			for ( int i = 0; i < clump.Positions.Length; i++ )
			{
				var p = clump.Positions[i];
				min = Vector3.Min( min, p );
				max = Vector3.Max( max, p );
				float wind = clump.Wind is not null && i < clump.Wind.Length ? clump.Wind[i] : 0f;
				vertices.Add( new AwGrassVertex
				{
					Position = p,
					Normal = Vector3.Up,
					Tangent = new Vector4( 1, 0, 0, 1 ),
					TexCoord = clump.Uvs[i],
					Color = new Color( wind, 0, 0, 1 ),
				} );
			}

			var mesh = new Mesh( AwMaterials.ForGrass( Host, grass.Atlas ), MeshPrimitiveType.Triangles );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( clump.Indices.Length, new List<int>( clump.Indices ) );
			mesh.Bounds = new BBox( min, max );

			var builder = Model.Builder.WithName( Path );
			builder.AddMesh( mesh );
			return builder.Create();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] grass clump {index} failed: {ex.Message}" );
			return null;
		}
	}
}

/// <summary>
/// Packs what <c>AwGrassRenderer</c> needs to grow a cell's grass at runtime, per terrain poly that has any:
///   u16 poly index (random seed), f32 corners[4][3] (s&amp;box space), f32 displacement[4][3] (corner normals in s&amp;box
///   space, inches per height unit), s16 heights[256], u8 layer codes[64], u8 clumps[6], u8 densities[6] (x16),
///   u8 brightness[256] (the terrain's baked brightness per grid cell).
/// Corners follow the terrain grid: (0,0), (1,0), (1,1), (0,1); triangles collapse the second corner onto the first.
/// </summary>
static class AwGrassData
{
	public const int RecordSize = 2 + 48 + 48 + 512 + 64 + 6 + 6 + 256;

	public static string Build( AwTerrainFile terrain, out int polyCount )
	{
		polyCount = 0;
		if ( terrain.GrassBlocks is null || terrain.Heights is null )
			return null;

		var blend = terrain.HdBlend ?? terrain.Blend;
		using var stream = new MemoryStream();
		using var w = new BinaryWriter( stream );

		for ( int i = 0; i < terrain.Polys.Length; i++ )
		{
			var block = i < terrain.GrassBlocks.Length ? terrain.GrassBlocks[i] : null;
			if ( block is null || block.Length < 76 || !HasGrass( block ) )
				continue;

			var p = terrain.Polys[i];
			var corners = p.IsTriangle ? new[] { p.V0, p.V0, p.V2, p.V1 } : new[] { p.V0, p.V1, p.V2, p.V3 };
			if ( Array.Exists( corners, v => v < 0 || v >= terrain.Positions.Length ) )
				continue;

			w.Write( (ushort)i );
			foreach ( var v in corners )
			{
				var position = AwSpace.Position( terrain.Positions[v] );
				w.Write( position.x ); w.Write( position.y ); w.Write( position.z );
			}
			foreach ( var v in corners )
			{
				var n = terrain.Normals[v];
				var displacement = AwSpace.Direction( n.x, n.y, n.z ) * (AwSpace.Scale / 2048f);
				w.Write( displacement.x ); w.Write( displacement.y ); w.Write( displacement.z );
			}

			var heights = i < terrain.Heights.Length ? terrain.Heights[i] : null;
			for ( int k = 0; k < 256; k++ )
				w.Write( heights is not null ? heights[k] : (short)0 );

			w.Write( block, 0, 76 );

			for ( int r = 0; r < 16; r++ )
			for ( int c = 0; c < 16; c++ )
				w.Write( Brightness( blend, i, (c + 0.5f) / 16f, (r + 0.5f) / 16f ) );

			polyCount++;
		}

		return polyCount > 0 ? Convert.ToBase64String( stream.ToArray() ) : null;
	}

	/// <summary>Any slot with a density, on a layer the poly's grid actually uses.</summary>
	static bool HasGrass( byte[] block )
	{
		Span<bool> used = stackalloc bool[3];
		for ( int k = 0; k < 256; k++ )
		{
			int code = (block[k >> 2] >> ((k & 3) * 2)) & 3;
			if ( code < 3 ) used[code] = true;
		}

		for ( int layer = 0; layer < 3; layer++ )
			if ( used[layer] && (block[64 + 6 + layer * 2] > 0 || block[64 + 6 + layer * 2 + 1] > 0) )
				return true;
		return false;
	}

	/// <summary>The blend map's baked brightness (bits 0-4) at a poly coordinate, as a byte.</summary>
	static byte Brightness( AwTerrainFile.PolyMaps blend, int poly, float s, float t )
	{
		var map = blend is not null && poly < blend.Maps.Length ? blend.Maps[poly] : null;
		if ( map is null )
			return 255;

		int x = Math.Clamp( (int)blend.Coord( s ), 0, blend.Dim - 1 );
		int y = Math.Clamp( (int)blend.Coord( t ), 0, blend.Dim - 1 );
		return (byte)((map[y * blend.Dim + x] & 31) * 255 / 31);
	}
}
