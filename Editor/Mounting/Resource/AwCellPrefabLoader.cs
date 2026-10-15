using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// One world cell as a prefab: the cell's terrain plus every static mesh placed in its high and low detail
/// data (low detail cells mostly hold water planes, rivers and landmark structures).
/// </summary>
sealed class AwCellPrefabLoader( AwCellInfo cell ) : ResourceLoader<AlanWakeMount>
{
	PrefabFile _prefab;

	protected override object Load()
	{
		try
		{
			var builder = new PrefabBuilder().WithName( Path );
			using ( builder.Scope() )
			{
				var root = new GameObject { Name = $"cell_{cell.Name}" };

				var terrain = new GameObject { Name = "terrain", Parent = root };
				var terrainModel = Model.Load( cell.TerrainModelPath );
				var renderer = terrain.AddComponent<ModelRenderer>();
				renderer.Model = terrainModel;
				if ( AlanWakeMount.Collision && cell.HighDetailPath is not null )
				{
					var collider = terrain.AddComponent<ModelCollider>();
					collider.Model = terrainModel;
					collider.Static = true;
				}

				if ( cell.HighDetailPath is not null )
					AwStaticPlacer.AddStaticObjects( Host, root, cell.HighDetailPath, "statics" );
				if ( cell.LowDetailPath is not null )
					AwStaticPlacer.AddStaticObjects( Host, root, cell.LowDetailPath, "distant" );
			}

			_prefab = builder.Create();
			return _prefab;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] cell prefab {cell.PrefabResourcePath} failed: {ex.Message}" );
			return null;
		}
	}

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		_prefab = null;
	}
}

/// <summary>Level wide statics (global.bin) and interiors (*_hull_*.bin etc.) of one level.</summary>
sealed class AwExtrasPrefabLoader( string level, List<string> files ) : ResourceLoader<AlanWakeMount>
{
	PrefabFile _prefab;

	protected override object Load()
	{
		try
		{
			var builder = new PrefabBuilder().WithName( Path );
			using ( builder.Scope() )
			{
				var root = new GameObject { Name = $"{level}_extras" };
				foreach ( var file in files )
					AwStaticPlacer.AddStaticObjects( Host, root, file, System.IO.Path.GetFileNameWithoutExtension( file ) );
			}

			_prefab = builder.Create();
			return _prefab;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] extras prefab {Path} failed: {ex.Message}" );
			return null;
		}
	}

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		_prefab = null;
	}
}

static class AwStaticPlacer
{
	/// <summary>Adds a child group holding one GameObject per static mesh found in a cell container.</summary>
	public static int AddStaticObjects( AlanWakeMount host, GameObject parent, string containerPath, string groupName )
	{
		Dictionary<string, byte[]> blobs;
		try
		{
			blobs = AwCellFile.Read( host.Archive.Read( containerPath ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] '{containerPath}': {ex.Message}" );
			return 0;
		}

		if ( !blobs.TryGetValue( "cid_staticobject.bin", out var data ) )
			return 0;

		List<AwStaticObject> objects;
		try
		{
			objects = AwStaticObjects.Read( data );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] '{containerPath}' statics: {ex.Message}" );
			return 0;
		}

		if ( objects.Count == 0 )
			return 0;

		var group = new GameObject { Name = groupName, Parent = parent };
		int added = 0;
		foreach ( var obj in objects )
		{
			if ( !host.Archive.TryGetPath( obj.MeshId, out var meshPath ) )
				continue;

			var modelPath = AlanWakeMount.ModelPathForMesh( meshPath );
			if ( modelPath is null )
				continue;

			var model = Model.Load( modelPath );
			if ( model is null || model.IsError )
				continue;

			var go = new GameObject
			{
				Name = System.IO.Path.GetFileNameWithoutExtension( meshPath ),
				Parent = group,
				LocalTransform = AwSpace.Transform( obj.Rotation, obj.Position ),
			};

			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = model;

			if ( AlanWakeMount.Collision )
			{
				var collider = go.AddComponent<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}

			added++;
		}

		return added;
	}
}
