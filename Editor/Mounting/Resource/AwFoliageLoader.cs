using System;
using System.Collections.Generic;

namespace Sandbox.Mounting;

/// <summary>
/// Models for one foliage type (.binfol): the full mesh, or (impostor = true) two crossed quads showing the
/// type's picture in the game's billboard atlas, used for distant copies.
/// </summary>
sealed class AwFoliageModelLoader( string archivePath, bool impostor ) : ResourceLoader<AlanWakeMount>
{
	protected override object Load()
	{
		try
		{
			var foliage = Host.GetFoliage( archivePath );
			if ( foliage is null )
				return null;

			var builder = Model.Builder.WithName( Path );
			return impostor ? BuildImpostor( builder, foliage ) : BuildMesh( builder, foliage );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] foliage '{archivePath}' failed: {ex.Message}" );
			return null;
		}
	}

	Model BuildMesh( ModelBuilder builder, AwFoliageFile foliage )
	{
		int meshes = 0;
		foreach ( var sub in foliage.Mesh.SubMeshes )
		{
			if ( sub.Lod != 0 || sub.Positions.Length == 0 || sub.Indices.Length == 0 )
				continue;

			var def = sub.MaterialIndex < foliage.Mesh.Materials.Count ? foliage.Mesh.Materials[sub.MaterialIndex] : null;
			var material = def is null ? Material.Load( "materials/dev/dev_measure.vmat" ) : AwMaterials.ForMesh( Host, def );
			builder.AddMesh( AwModelLoader.BuildMesh( material, sub, out _ ) );
			meshes++;
		}

		return meshes > 0 ? builder.Create() : null;
	}

	Model BuildImpostor( ModelBuilder builder, AwFoliageFile foliage )
	{
		if ( foliage.BillboardWidth <= 0 || foliage.BillboardHeight <= 0 || string.IsNullOrEmpty( foliage.BillboardAtlas ) )
			return null;

		float w = foliage.BillboardWidth * AwSpace.Scale * 0.5f;
		float h = foliage.BillboardHeight * AwSpace.Scale;
		var r = foliage.AtlasRect;
		float u0 = r.x, v0 = r.y, u1 = r.x + r.z, v1 = r.y + r.w;

		var sub = new AwMeshFile.SubMesh
		{
			Positions = new[]
			{
				new Vector3( -w, 0, 0 ), new Vector3( w, 0, 0 ), new Vector3( w, 0, h ), new Vector3( -w, 0, h ),
				new Vector3( 0, -w, 0 ), new Vector3( 0, w, 0 ), new Vector3( 0, w, h ), new Vector3( 0, -w, h ),
			},
			Uv0 = new[]
			{
				new Vector2( u0, v1 ), new Vector2( u1, v1 ), new Vector2( u1, v0 ), new Vector2( u0, v0 ),
				new Vector2( u0, v1 ), new Vector2( u1, v1 ), new Vector2( u1, v0 ), new Vector2( u0, v0 ),
			},
			Indices = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 },
		};
		sub.Normals = new Vector3[sub.Positions.Length];
		Array.Fill( sub.Normals, Vector3.Up );

		builder.AddMesh( AwModelLoader.BuildMesh( AwMaterials.ForImpostor( Host, foliage.BillboardAtlas ), sub, out _ ) );
		return builder.Create();
	}
}
