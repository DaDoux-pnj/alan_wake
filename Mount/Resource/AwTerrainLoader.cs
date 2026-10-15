using System;
using System.Collections.Generic;

namespace Sandbox.Mounting;

/// <summary>
/// Builds the terrain model of one 128 m world cell: the low detail quad mesh, displaced by the high detail
/// height grids, lit with the game's terrain normal maps and textured with its 3 layer height blend
/// (shaders/aw_terrain.shader). LOD1 keeps only the displaced quad corners.
/// </summary>
sealed class AwTerrainLoader( AwCellInfo cell ) : ResourceLoader<AlanWakeMount>
{
	sealed class Batch
	{
		public readonly List<AwTerrainVertex> Vertices = new();
		public readonly List<int> Indices = new();
	}

	protected override object Load()
	{
		try
		{
			var ld = AwCellFile.Read( Host.Archive.Read( cell.LowDetailPath ) );
			if ( !ld.TryGetValue( "cid_terraindata.bin", out var ldTerrain ) )
				return null;

			var terrain = AwTerrainFile.ReadLowDetail( ldTerrain );
			if ( terrain.Polys.Length == 0 )
				return null;

			if ( cell.HighDetailPath is not null )
			{
				try
				{
					var hd = AwCellFile.Read( Host.Archive.Read( cell.HighDetailPath ) );
					if ( hd.TryGetValue( "cid_terraindata.bin", out var hdTerrain ) )
						terrain.ReadHighDetail( hdTerrain );
				}
				catch ( Exception ex )
				{
					Log.Warning( $"[AW] HD terrain '{cell.HighDetailPath}': {ex.Message}" );
				}
			}

			var blend = terrain.HdBlend ?? terrain.Blend;
			var normalMaps = terrain.HdNormalMaps ?? terrain.NormalMaps;
			var atlas = BuildAtlas( blend, terrain.Polys.Length, out int cols, out int atlasW, out int atlasH );

			// Background cells (low detail only) have no height grids: subdividing them adds nothing, and nobody
			// can walk on them, so they get flat quads and no collision.
			bool detailed = terrain.Heights is not null;
			int subdivisions = SubdivisionsFor( detailed );
			bool useLod = AlanWakeMount.TerrainLodDistance > 0 && subdivisions > 1;
			bool collide = AlanWakeMount.Collision && detailed;

			var lod0 = Generate( terrain, subdivisions, blend, normalMaps, cols, atlasW, atlasH );
			var lod1 = useLod ? Generate( terrain, 1, blend, normalMaps, cols, atlasW, atlasH ) : null;

			var builder = Model.Builder.WithName( Path );
			var materials = new Dictionary<int, Material>();
			Material MaterialFor( int index )
			{
				if ( materials.TryGetValue( index, out var m ) ) return m;
				var def = index < terrain.Materials.Length ? terrain.Materials[index] : new ushort[6];
				string Tex( int k ) => def[k] < terrain.Textures.Length ? terrain.Textures[def[k]] : null;
				m = AwMaterials.ForTerrain( Host, Tex( 0 ), Tex( 1 ), Tex( 2 ), atlas, $"{cell.TerrainResourcePath}|{index}" );
				materials[index] = m;
				return m;
			}

			var collisionPositions = new List<Vector3>();
			var collisionIndices = new List<int>();

			foreach ( var (materialIndex, batch) in lod0 )
			{
				var mesh = CreateMesh( MaterialFor( materialIndex ), batch );
				if ( useLod ) builder.AddMesh( mesh, 0 );
				else builder.AddMesh( mesh );

				if ( !collide )
					continue;

				int baseIndex = collisionPositions.Count;
				foreach ( var v in batch.Vertices ) collisionPositions.Add( v.Position );
				foreach ( var i in batch.Indices ) collisionIndices.Add( baseIndex + i );
			}

			if ( useLod )
			{
				foreach ( var (materialIndex, batch) in lod1 )
					builder.AddMesh( CreateMesh( MaterialFor( materialIndex ), batch ), 1 );

				builder.WithLodDistance( 0, 0 );
				builder.WithLodDistance( 1, AlanWakeMount.TerrainLodDistance );
				for ( int lod = 2; lod < 8; lod++ )
					builder.WithLodDistance( lod, 1_000_000f );
			}

			if ( collide && collisionIndices.Count > 0 )
				builder.AddCollisionMesh( collisionPositions, collisionIndices );

			if ( AlanWakeMount.DebugLog )
				Log.Info( $"[AW] terrain {cell.World}/{cell.Level}/{cell.Name}: {terrain.Polys.Length} polys, {materials.Count} materials, hd={terrain.Heights is not null}, hd maps={terrain.HdBlend is not null}" );

			return builder.Create();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] terrain {cell.TerrainResourcePath} failed: {ex.Message}" );
			return null;
		}
	}

	/// <summary>Grid segments per terrain poly edge (the grass renderer stands on the same surface).</summary>
	internal static int SubdivisionsFor( bool detailed )
		=> !detailed ? 1 : AlanWakeMount.TerrainSubdivisions switch { >= 15 => 15, >= 5 => 5, >= 3 => 3, _ => 1 };

	static Mesh CreateMesh( Material material, Batch batch )
	{
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		foreach ( var v in batch.Vertices )
		{
			min = Vector3.Min( min, v.Position );
			max = Vector3.Max( max, v.Position );
		}

		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer( batch.Vertices.Count, batch.Vertices );
		mesh.CreateIndexBuffer( batch.Indices.Count, batch.Indices );
		mesh.Bounds = new BBox( min, max );
		return mesh;
	}

	/// <summary>
	/// Packs every poly's blend map into one RGBA atlas: R/G = layer thresholds, B = baked brightness.
	/// Without blend maps every sample shows the base layer at full brightness.
	/// </summary>
	static Texture BuildAtlas( AwTerrainFile.PolyMaps blend, int polyCount, out int cols, out int width, out int height )
	{
		int dim = blend?.Dim ?? 1;
		cols = Math.Max( 1, (int)MathF.Ceiling( MathF.Sqrt( Math.Max( 1, polyCount ) ) ) );
		int rows = Math.Max( 1, (polyCount + cols - 1) / cols );
		width = cols * dim;
		height = rows * dim;

		var data = new byte[width * height * 4];
		for ( int i = 0; i < polyCount; i++ )
		{
			var map = blend is not null && i < blend.Maps.Length ? blend.Maps[i] : null;
			int tx = (i % cols) * dim, ty = (i / cols) * dim;
			for ( int r = 0; r < dim; r++ )
			for ( int c = 0; c < dim; c++ )
			{
				ushort v = map is not null && r * dim + c < map.Length ? map[r * dim + c] : (ushort)0x001F;
				int o = ((ty + r) * width + tx + c) * 4;
				data[o + 0] = (byte)(((v >> 10) & 31) * 255 / 31);
				data[o + 1] = (byte)(((v >> 5) & 31) * 255 / 31);
				data[o + 2] = (byte)((v & 31) * 255 / 31);
				data[o + 3] = 255;
			}
		}

		return Texture.Create( width, height, ImageFormat.RGBA8888 )
			.WithData( data )
			.WithMips()
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>A poly's vertices at its grid corners (0,0), (1,0), (1,1), (0,1), and the corner records they come from.</summary>
	static void Corners( AwTerrainFile.Poly p, Span<int> vertex, Span<int> slot )
	{
		if ( p.IsTriangle )
		{
			// The grid of a triangle has v0 at (0,0), v1 at (15,0), v2 at (15,15); (0,15) collapses onto v0.
			vertex[0] = p.V0; vertex[1] = p.V0; vertex[2] = p.V2; vertex[3] = p.V1;
			slot[0] = 0; slot[1] = 0; slot[2] = 2; slot[3] = 1;
		}
		else
		{
			vertex[0] = p.V0; vertex[1] = p.V1; vertex[2] = p.V2; vertex[3] = p.V3;
			slot[0] = 0; slot[1] = 1; slot[2] = 2; slot[3] = 3;
		}
	}

	/// <summary>
	/// The displaced 16 x 16 surface grid of one poly (row = t, column = s) in Alan Wake space, the points the mesh
	/// and its collision are built from; null when the poly's corners are broken.
	/// </summary>
	internal static Vector3[] SurfaceGrid( AwTerrainFile t, int poly )
	{
		Span<int> vertex = stackalloc int[4];
		Span<int> slot = stackalloc int[4];
		Corners( t.Polys[poly], vertex, slot );
		foreach ( var v in vertex )
			if ( v < 0 || v >= t.Positions.Length ) return null;

		var heights = t.Heights is not null && poly < t.Heights.Length ? t.Heights[poly] : null;
		var grid = new Vector3[256];
		for ( int r = 0; r < 16; r++ )
		for ( int c = 0; c < 16; c++ )
		{
			float s = c / 15f, tt = r / 15f;
			var pos = Bilinear( t.Positions[vertex[0]], t.Positions[vertex[1]], t.Positions[vertex[2]], t.Positions[vertex[3]], s, tt );
			if ( heights is not null )
				pos += Bilinear( t.Normals[vertex[0]], t.Normals[vertex[1]], t.Normals[vertex[2]], t.Normals[vertex[3]], s, tt ) * (heights[r * 16 + c] / 2048f);
			grid[r * 16 + c] = pos;
		}

		return grid;
	}

	static Vector3 Bilinear( Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float s, float t )
	{
		var a = p0 + (p1 - p0) * s;
		var b = p3 + (p2 - p3) * s;
		return a + (b - a) * t;
	}

	static Vector4 TangentFor( Vector3 normal )
	{
		var tangent = Vector3.Cross( normal, Vector3.Left );
		if ( tangent.LengthSquared < 1e-6f ) tangent = Vector3.Cross( normal, Vector3.Forward );
		return new Vector4( tangent.Normal, 1 );
	}

	static Dictionary<int, Batch> Generate( AwTerrainFile t, int segments, AwTerrainFile.PolyMaps blend, AwTerrainFile.PolyMaps normalMaps,
		int cols, int atlasW, int atlasH )
	{
		var batches = new Dictionary<int, Batch>();
		int stride = segments + 1;
		int dim = blend?.Dim ?? 1;
		var cornerVertex = new int[4];
		var cornerSlot = new int[4];
		var luv = new float[4, 6];
		const int period = AwTerrainFile.LayerUvPeriod;

		for ( int i = 0; i < t.Polys.Length; i++ )
		{
			var p = t.Polys[i];
			Corners( p, cornerVertex, cornerSlot );

			bool valid = true;
			for ( int k = 0; k < 4; k++ )
				if ( cornerVertex[k] < 0 || cornerVertex[k] >= t.Positions.Length ) valid = false;
			if ( !valid ) continue;

			var P0 = t.Positions[cornerVertex[0]]; var P1 = t.Positions[cornerVertex[1]];
			var P2 = t.Positions[cornerVertex[2]]; var P3 = t.Positions[cornerVertex[3]];
			var N0 = t.Normals[cornerVertex[0]]; var N1 = t.Normals[cornerVertex[1]];
			var N2 = t.Normals[cornerVertex[2]]; var N3 = t.Normals[cornerVertex[3]];

			// Layer uvs wrap at 16 bits; unwrap every corner relative to corner 0 (keeping its offset within one
			// texture repeat), then scale to repeats.
			for ( int layer = 0; layer < 3; layer++ )
			{
				int u0 = p.CornerUv[cornerSlot[0] * 8 + layer * 2];
				int v0 = p.CornerUv[cornerSlot[0] * 8 + layer * 2 + 1];
				int baseU = u0 & ~(period - 1), baseV = v0 & ~(period - 1);
				for ( int k = 0; k < 4; k++ )
				{
					int u = p.CornerUv[cornerSlot[k] * 8 + layer * 2];
					int v = p.CornerUv[cornerSlot[k] * 8 + layer * 2 + 1];
					luv[k, layer * 2] = (u0 - baseU + (short)(u - u0)) / (float)period;
					luv[k, layer * 2 + 1] = (v0 - baseV + (short)(v - v0)) / (float)period;
				}
			}

			// The game's triangles (v0, v1, v2) face along their normal; mirrored grids (triangles) need a flip.
			var dPds = (P1 - P0 + P2 - P3) * 0.5f;
			var dPdt = (P3 - P0 + P2 - P1) * 0.5f;
			bool flip = Vector3.Dot( Vector3.Cross( dPds, dPdt ), N0 + N1 + N2 + N3 ) < 0;

			var heights = t.Heights is not null && i < t.Heights.Length ? t.Heights[i] : null;
			if ( !batches.TryGetValue( p.Material, out var batch ) )
				batches[p.Material] = batch = new Batch();

			int baseIndex = batch.Vertices.Count;
			int tileX = (i % cols) * dim, tileY = (i / cols) * dim;

			for ( int r = 0; r <= segments; r++ )
			for ( int c = 0; c <= segments; c++ )
			{
				int gr = r * 15 / segments, gc = c * 15 / segments;
				float s = gc / 15f, tt = gr / 15f;

				var pos = Bilinear( P0, P1, P2, P3, s, tt );
				var nrm = Bilinear( N0, N1, N2, N3, s, tt );
				if ( heights is not null )
					pos += nrm * (heights[gr * 16 + gc] / 2048f);

				// The game lights its terrain with these world space normal maps, not the mesh normals.
				var mapNormal = normalMaps?.SampleNormal( i, s, tt ) ?? Vector3.Zero;
				var normal = mapNormal.LengthSquared > 0.5f
					? AwSpace.Direction( mapNormal.x, mapNormal.y, mapNormal.z ).Normal
					: AwSpace.Direction( nrm.x, nrm.y, nrm.z ).Normal;

				float w00 = (1 - s) * (1 - tt), w10 = s * (1 - tt), w11 = s * tt, w01 = (1 - s) * tt;
				float U( int k ) => luv[0, k] * w00 + luv[1, k] * w10 + luv[2, k] * w11 + luv[3, k] * w01;

				float ax = (tileX + (blend?.Coord( s ) ?? 0.5f)) / atlasW;
				float ay = (tileY + (blend?.Coord( tt ) ?? 0.5f)) / atlasH;

				batch.Vertices.Add( new AwTerrainVertex
				{
					Position = AwSpace.Position( pos ),
					Normal = normal,
					Tangent = TangentFor( normal ),
					TexCoord0 = new Vector2( U( 0 ), U( 1 ) ),
					TexCoord1 = new Vector2( U( 2 ), U( 3 ) ),
					LayerUv01 = new Vector4( U( 0 ), U( 1 ), U( 2 ), U( 3 ) ),
					LayerUv2Blend = new Vector4( U( 4 ), U( 5 ), ax, ay ),
				} );
			}

			for ( int r = 0; r < segments; r++ )
			for ( int c = 0; c < segments; c++ )
			{
				int q00 = baseIndex + r * stride + c, q01 = q00 + 1, q10 = q00 + stride, q11 = q10 + 1;
				// Game order is (q00, q01, q11) + (q00, q11, q10); s&box needs it reversed (Y/Z swap mirrors space).
				if ( !flip )
				{
					batch.Indices.Add( q00 ); batch.Indices.Add( q11 ); batch.Indices.Add( q01 );
					batch.Indices.Add( q00 ); batch.Indices.Add( q10 ); batch.Indices.Add( q11 );
				}
				else
				{
					batch.Indices.Add( q00 ); batch.Indices.Add( q01 ); batch.Indices.Add( q11 );
					batch.Indices.Add( q00 ); batch.Indices.Add( q11 ); batch.Indices.Add( q10 );
				}
			}
		}

		if ( normalMaps is null )
			SmoothNormals( batches );
		return batches;
	}

	/// <summary>Fallback when a cell has no normal maps: normals from the displaced surface, shared across quad borders.</summary>
	static void SmoothNormals( Dictionary<int, Batch> batches )
	{
		var sums = new Dictionary<(int, int, int), Vector3>();
		static (int, int, int) Key( Vector3 p ) => ((int)MathF.Round( p.x * 4 ), (int)MathF.Round( p.y * 4 ), (int)MathF.Round( p.z * 4 ));

		foreach ( var batch in batches.Values )
		{
			var v = batch.Vertices;
			var idx = batch.Indices;
			for ( int i = 0; i + 2 < idx.Count; i += 3 )
			{
				var a = v[idx[i]].Position; var b = v[idx[i + 1]].Position; var c = v[idx[i + 2]].Position;
				var n = Vector3.Cross( b - a, c - a );
				foreach ( var p in new[] { a, b, c } )
				{
					var k = Key( p );
					sums[k] = sums.TryGetValue( k, out var s ) ? s + n : n;
				}
			}
		}

		foreach ( var batch in batches.Values )
		{
			var v = batch.Vertices;
			for ( int i = 0; i < v.Count; i++ )
			{
				var vert = v[i];
				if ( sums.TryGetValue( Key( vert.Position ), out var n ) && n.LengthSquared > 1e-12f )
				{
					// Keep the game's orientation: only accept the recomputed normal if it agrees.
					var nn = n.Normal;
					if ( Vector3.Dot( nn, vert.Normal ) < 0 ) nn = -nn;
					vert.Normal = nn;
				}

				vert.Tangent = TangentFor( vert.Normal );
				v[i] = vert;
			}
		}
	}
}
