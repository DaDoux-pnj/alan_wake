using System;
using System.Collections.Generic;

namespace Sandbox.Mounting;

/// <summary>
/// Builds an s&amp;box model from one .binmsh (LOD0 + the game's LOD1 when present). Static models collide with
/// their triangles; physics models (used for the game's dynamic objects) get a convex hull and a mass instead,
/// since triangle meshes can't move. Physics models bigger than <see cref="LoosePropMaxSize"/> (vehicles, logs,
/// bridge parts) keep triangle collision: the placer checks for a hull to decide whether to add a rigidbody.
/// </summary>
sealed class AwModelLoader( string archivePath, bool physics = false ) : ResourceLoader<AlanWakeMount>
{
	/// <summary>Largest LOD0 bounding box diagonal (metres) that still makes a loose physics prop.</summary>
	public const float LoosePropMaxSize = 3f;

	/// <summary>Inches. No prop stops drawing closer than this, however small.</summary>
	const float MinDrawDistance = 800f;

	/// <summary>
	/// The engine picks LODs by screen size (CalculateLODLevel): a switch value of 1 is about 19 inches away at 1920
	/// pixels wide and a 90 degree field of view, proportionally further at higher resolutions.
	/// </summary>
	const float InchesPerLodUnit = 19.2f;

	protected override object Load()
	{
		try
		{
			var file = AwMeshFile.Parse( Host.Archive.Read( archivePath ) );
			var builder = Model.Builder.WithName( Path );

			bool hasLod1 = file.LodCount > 1;
			bool hasDrawDistance = AlanWakeMount.PropDrawDistance > 0;
			var collisionPositions = new List<Vector3>();
			var collisionIndices = new List<int>();
			var bounds = new BBox( Vector3.Zero, Vector3.Zero );
			bool boundsSet = false;
			int meshCount = 0;

			foreach ( var sub in file.SubMeshes )
			{
				if ( sub.Positions.Length == 0 || sub.Indices.Length == 0 )
					continue;
				if ( sub.Lod > 1 )
					continue;

				var def = sub.MaterialIndex < file.Materials.Count ? file.Materials[sub.MaterialIndex] : null;
				var material = def is null ? Material.Load( "materials/dev/dev_measure.vmat" ) : AwMaterials.ForMesh( Host, def );
				bool water = AwMaterials.IsWater( def );
				BBox subBounds;
				var mesh = water ? BuildWaterMesh( material, sub, def, out subBounds ) : BuildMesh( material, sub, out subBounds );

				if ( hasLod1 || hasDrawDistance ) builder.AddMesh( mesh, sub.Lod );
				else builder.AddMesh( mesh );
				meshCount++;

				if ( sub.Lod == 0 )
				{
					bounds = boundsSet ? bounds.AddBBox( subBounds ) : subBounds;
					boundsSet = true;

					// Glass still counts for physics props: a bottle's hull is the whole bottle. Water never collides.
					bool solid = def is null || !(def.Translucent || def.Shader.StartsWith( "Glass", StringComparison.OrdinalIgnoreCase ));
					if ( (solid || physics) && !water && AlanWakeMount.Collision )
					{
						int baseIndex = collisionPositions.Count;
						collisionPositions.AddRange( sub.Positions );
						foreach ( var i in sub.Indices ) collisionIndices.Add( baseIndex + i );
					}
				}
			}

			if ( meshCount == 0 )
				return null;

			if ( hasLod1 || hasDrawDistance )
			{
				// Bigger props keep their full detail mesh for longer, and draw further: the game gives every object a
				// draw distance, here the LOD after the last mesh, which is empty (aw_prop_draw_distance times the
				// prop's size, so houses outlast chairs). Unused levels sit out of reach, or the engine's defaults
				// (one every 50 units) would empty the model up close.
				float size = boundsSet ? bounds.Size.Length : 0;
				float drawDistance = hasDrawDistance
					? MathF.Max( size * AlanWakeMount.PropDrawDistance, MinDrawDistance ) / InchesPerLodUnit
					: 1_000_000f;
				int empty = hasLod1 ? 2 : 1;

				builder.WithLodDistance( 0, 0 );
				if ( hasLod1 )
					builder.WithLodDistance( 1, MathF.Min( MathF.Max( AlanWakeMount.MeshLodDistance, size * 6f ), drawDistance ) );
				builder.WithLodDistance( empty, drawDistance );
				for ( int lod = empty + 1; lod < 8; lod++ )
					builder.WithLodDistance( lod, 1_000_000f );
			}

			if ( physics && collisionPositions.Count >= 4 && bounds.Size.Length <= LoosePropMaxSize * AwSpace.Scale )
			{
				var size = bounds.Size / AwSpace.Scale;
				float volume = MathF.Max( size.x, 0.05f ) * MathF.Max( size.y, 0.05f ) * MathF.Max( size.z, 0.05f );
				builder.AddCollisionHull( collisionPositions );
				builder.WithMass( Math.Clamp( volume * 150f, 1f, 400f ) );
			}
			else if ( collisionIndices.Count > 0 )
			{
				builder.AddCollisionMesh( collisionPositions, collisionIndices );
			}

			return builder.Create();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] mesh '{archivePath}' failed: {ex.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Water surface for shaders/aw_water.shader. River.rfx scrolls its colour map towards -V at g_fVelocity repeats of
	/// g_vTexCoordScale.y per second, so the water runs along -dP/dv at velocity / scale.y * |dP/dv| world units per
	/// second: the tangent is laid downstream and the colour carries that speed. Lakes (Water.rfx) are still. Steep river
	/// pieces (waterfalls) churn with foam, the rest carries a little.
	/// </summary>
	internal static Mesh BuildWaterMesh( Material material, AwMeshFile.SubMesh sub, AwMeshFile.MaterialDef def, out BBox bounds )
	{
		int n = sub.Positions.Length;
		var idx = sub.Indices;
		bool river = def.Shader.StartsWith( "River", StringComparison.OrdinalIgnoreCase );
		var dPdv = new Vector3[n];
		var counts = new int[n];

		if ( river )
		{
			for ( int t = 0; t + 2 < idx.Length; t += 3 )
			{
				int a = idx[t], b = idx[t + 1], c = idx[t + 2];
				if ( a >= n || b >= n || c >= n ) continue;
				var e1 = sub.Positions[b] - sub.Positions[a];
				var e2 = sub.Positions[c] - sub.Positions[a];
				var d1 = sub.Uv0[b] - sub.Uv0[a];
				var d2 = sub.Uv0[c] - sub.Uv0[a];
				float det = d1.x * d2.y - d2.x * d1.y;
				if ( MathF.Abs( det ) < 1e-12f ) continue;
				var along = (e2 * d1.x - e1 * d2.x) / det;
				dPdv[a] += along; dPdv[b] += along; dPdv[c] += along;
				counts[a]++; counts[b]++; counts[c]++;
			}
		}

		var texScale = def.Vector( "g_vTexCoordScale" );
		float scaleV = texScale is { Length: >= 2 } && texScale[1] > 0 ? texScale[1] : 1f;
		float velocity = def.Value( "g_fVelocity", 0f );

		var verts = new List<AwWaterVertex>( n );
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		for ( int i = 0; i < n; i++ )
		{
			var normal = sub.Normals[i];
			float speed = 0;
			var downstream = Vector3.Forward;
			if ( counts[i] > 0 )
			{
				var gradient = dPdv[i] / counts[i];
				speed = velocity / scaleV * gradient.Length;
				downstream = -gradient;
			}

			var tangent = downstream - normal * Vector3.Dot( normal, downstream );
			if ( tangent.LengthSquared < 1e-8f )
				tangent = MathF.Abs( normal.z ) < 0.9f ? Vector3.Cross( Vector3.Up, normal ) : Vector3.Cross( normal, Vector3.Left );

			float foam = !river ? 0f : MathF.Abs( normal.z ) < 0.6f ? 1f : 0.2f;

			var p = sub.Positions[i];
			min = Vector3.Min( min, p );
			max = Vector3.Max( max, p );

			verts.Add( new AwWaterVertex
			{
				Position = p,
				Normal = normal,
				Tangent = new Vector4( tangent.Normal, 1 ),
				TexCoord = sub.Uv0[i],
				Color = new Color( speed, 1f, foam, 0f ),
			} );
		}

		bounds = new BBox( min, max );
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer( verts.Count, verts );
		mesh.CreateIndexBuffer( idx.Length, new List<int>( idx ) );
		mesh.Bounds = bounds;
		return mesh;
	}

	internal static Mesh BuildMesh( Material material, AwMeshFile.SubMesh sub, out BBox bounds )
	{
		int n = sub.Positions.Length;
		var tangents = new Vector3[n];
		var bitangents = new Vector3[n];
		var idx = sub.Indices;

		for ( int t = 0; t + 2 < idx.Length; t += 3 )
		{
			int a = idx[t], b = idx[t + 1], c = idx[t + 2];
			if ( a >= n || b >= n || c >= n ) continue;
			var e1 = sub.Positions[b] - sub.Positions[a];
			var e2 = sub.Positions[c] - sub.Positions[a];
			var d1 = sub.Uv0[b] - sub.Uv0[a];
			var d2 = sub.Uv0[c] - sub.Uv0[a];
			float det = d1.x * d2.y - d2.x * d1.y;
			if ( MathF.Abs( det ) < 1e-12f ) continue;
			float r = 1f / det;
			var tan = (e1 * d2.y - e2 * d1.y) * r;
			var bit = (e2 * d1.x - e1 * d2.x) * r;
			tangents[a] += tan; tangents[b] += tan; tangents[c] += tan;
			bitangents[a] += bit; bitangents[b] += bit; bitangents[c] += bit;
		}

		// Trees and plants carry the game's wind data in the vertex colour (shaders/aw_foliage.shader).
		var verts = sub.Wind is null ? new List<AwVertex>( n ) : null;
		var foliageVerts = sub.Wind is null ? null : new List<AwFoliageVertex>( n );
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		for ( int i = 0; i < n; i++ )
		{
			var normal = sub.Normals[i];
			var tangent = tangents[i] - normal * Vector3.Dot( normal, tangents[i] );
			if ( tangent.LengthSquared < 1e-12f )
				tangent = MathF.Abs( normal.z ) < 0.9f ? Vector3.Cross( Vector3.Up, normal ) : Vector3.Cross( Vector3.Forward, normal );
			tangent = tangent.Normal;
			float sign = Vector3.Dot( Vector3.Cross( normal, tangent ), bitangents[i] ) < 0 ? -1f : 1f;

			var p = sub.Positions[i];
			min = Vector3.Min( min, p );
			max = Vector3.Max( max, p );

			var uv = new Vector4( sub.Uv0[i].x, sub.Uv0[i].y, 0, 0 );
			if ( foliageVerts is not null )
			{
				var wind = sub.Wind[i];
				foliageVerts.Add( new AwFoliageVertex
				{
					Position = p,
					Normal = normal,
					Tangent = new Vector4( tangent, sign ),
					TexCoord = uv,
					Color = new Color( wind.x, wind.y, wind.z, wind.w ),
				} );
			}
			else
			{
				verts.Add( new AwVertex
				{
					Position = p,
					Normal = normal,
					Tangent = new Vector4( tangent, sign ),
					TexCoord = uv,
				} );
			}
		}

		bounds = new BBox( min, max );
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		if ( foliageVerts is not null )
			mesh.CreateVertexBuffer( foliageVerts.Count, foliageVerts );
		else
			mesh.CreateVertexBuffer( verts.Count, verts );
		mesh.CreateIndexBuffer( idx.Length, new List<int>( idx ) );
		mesh.Bounds = bounds;
		return mesh;
	}
}

/// <summary>Exposes game textures in the asset browser.</summary>
sealed class AwTextureLoader( string archivePath ) : ResourceLoader<AlanWakeMount>
{
	protected override object Load()
	{
		try
		{
			return TextureLoader.FromDds( AwTextureFile.ToDds( Host.Archive.Read( archivePath ) ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] texture '{archivePath}' failed: {ex.Message}" );
			return null;
		}
	}
}
