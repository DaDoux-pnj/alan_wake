using System;
using System.Collections.Generic;

namespace Sandbox.Mounting;

/// <summary>
/// One world cell as a prefab: terrain, static meshes, dynamic objects (loose props get physics), lights and
/// foliage. High detail data holds the playable content; low detail data holds distant landmarks/water and trees.
/// </summary>
sealed class AwCellPrefabLoader( AwCellInfo cell ) : ResourceLoader<AlanWakeMount>
{
	PrefabFile _prefab;

	protected override object Load()
	{
		try
		{
			var hd = AwStaticPlacer.ReadContainer( Host, cell.HighDetailPath );
			var ld = AwStaticPlacer.ReadContainer( Host, cell.LowDetailPath );

			var builder = new PrefabBuilder().WithName( Path );
			using ( builder.Scope() )
			{
				var root = new GameObject { Name = $"cell_{cell.Name}" };

				var terrain = new GameObject { Name = "terrain", Parent = root };
				var terrainModel = Model.Load( cell.TerrainModelPath );
				var renderer = terrain.AddComponent<ModelRenderer>();
				renderer.Model = terrainModel;
				if ( AlanWakeMount.Collision && hd is not null )
				{
					var collider = terrain.AddComponent<ModelCollider>();
					collider.Model = terrainModel;
					collider.Static = true;
				}

				if ( hd is not null )
				{
					AwStaticPlacer.AddStaticObjects( Host, root, hd, "statics" );
					AwStaticPlacer.AddDynamicObjects( Host, root, hd );
					AwStaticPlacer.AddLights( root, hd );
				}

				if ( ld is not null )
					AwStaticPlacer.AddStaticObjects( Host, root, ld, "distant" );

				if ( AlanWakeMount.Foliage )
				{
					// Trees live in the low detail data (so they show from afar), undergrowth in the high detail data.
					if ( ld is not null )
						AwStaticPlacer.AddFoliage( Host, root, ld, "trees", undergrowth: false );
					if ( hd is not null )
						AwStaticPlacer.AddFoliage( Host, root, hd, "undergrowth", undergrowth: true );
				}

				if ( AlanWakeMount.Grass && hd is not null && ld is not null )
					AwStaticPlacer.AddGrass( Host, root, ld, hd );
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

/// <summary>Level wide content (global.bin) and interiors (*_hull_*.bin etc.) of one level.</summary>
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
				{
					var blobs = AwStaticPlacer.ReadContainer( Host, file );
					if ( blobs is null )
						continue;

					var group = new GameObject { Name = System.IO.Path.GetFileNameWithoutExtension( file ), Parent = root };
					AwStaticPlacer.AddStaticObjects( Host, group, blobs, "statics" );
					AwStaticPlacer.AddDynamicObjects( Host, group, blobs );
					AwStaticPlacer.AddLights( group, blobs );
				}
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
	/// <summary>
	/// Name fragments of dynamic objects that the game hinges, hangs, animates or bolts down: kept static.
	/// Anything bigger than <see cref="AwModelLoader.LoosePropMaxSize"/> (vehicles, logs, rocks) is static anyway.
	/// </summary>
	static readonly string[] FixedDynamicNames = { "door", "gate", "fence", "sign", "hanging", "ceiling", "lantern", "lamp", "chain_", "handset", "hatch", "windbell", "bridge", "static", "_kf_" };

	public static Dictionary<string, byte[]> ReadContainer( AlanWakeMount host, string containerPath )
	{
		if ( containerPath is null )
			return null;

		try
		{
			return AwCellFile.Read( host.Archive.Read( containerPath ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] '{containerPath}': {ex.Message}" );
			return null;
		}
	}

	/// <summary>Adds a child group holding one GameObject per static mesh (cid_staticobject.bin).</summary>
	public static int AddStaticObjects( AlanWakeMount host, GameObject parent, Dictionary<string, byte[]> blobs, string groupName )
	{
		var objects = ReadObjects( blobs, "cid_staticobject.bin", AwStaticObjects.StaticRecordSize );
		if ( objects.Count == 0 )
			return 0;

		var group = new GameObject { Name = groupName, Parent = parent };
		int added = 0;
		foreach ( var obj in objects )
		{
			if ( !host.Archive.TryGetPath( obj.MeshId, out var meshPath ) )
				continue;

			var model = LoadModel( meshPath, false );
			if ( model is null )
				continue;

			var go = new GameObject
			{
				Name = System.IO.Path.GetFileNameWithoutExtension( meshPath ),
				Parent = group,
				LocalTransform = AwSpace.Transform( obj.Rotation, obj.Position ),
			};

			go.AddComponent<ModelRenderer>().Model = model;

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

	/// <summary>
	/// Adds the game's dynamic objects (cid_dynamicobject.bin). Small loose props get a convex physics model and a
	/// sleeping rigidbody; doors, gates, signs, hanging lamps and anything big (vehicles, logs) stay static.
	/// </summary>
	public static int AddDynamicObjects( AlanWakeMount host, GameObject parent, Dictionary<string, byte[]> blobs )
	{
		if ( !AlanWakeMount.DynamicObjects )
			return 0;

		var objects = ReadObjects( blobs, "cid_dynamicobject.bin", AwStaticObjects.DynamicRecordSize );
		if ( objects.Count == 0 )
			return 0;

		var group = new GameObject { Name = "dynamic", Parent = parent };
		int added = 0;
		foreach ( var obj in objects )
		{
			if ( !host.Archive.TryGetPath( obj.MeshId, out var meshPath ) )
				continue;

			var name = System.IO.Path.GetFileNameWithoutExtension( meshPath );
			bool physics = AlanWakeMount.DynamicPhysics && !FixedDynamicNames.Any( x => name.Contains( x, StringComparison.OrdinalIgnoreCase ) );

			var model = LoadModel( meshPath, physics );
			if ( model is null )
				continue;

			// Physics models of big objects fall back to triangle collision: those stay static.
			physics = physics && model.Physics?.Parts.Any( x => x.Hulls.Count > 0 ) == true;

			var go = new GameObject
			{
				Name = name,
				Parent = group,
				LocalTransform = AwSpace.Transform( obj.Rotation, obj.Position ),
			};

			go.AddComponent<ModelRenderer>().Model = model;

			if ( physics )
			{
				go.AddComponent<ModelCollider>().Model = model;
				var body = go.AddComponent<Rigidbody>();
				body.StartAsleep = true;
			}
			else if ( AlanWakeMount.Collision )
			{
				var collider = go.AddComponent<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}

			added++;
		}

		return added;
	}

	/// <summary>Adds the cell's point and spot lights (cid_pointlight.bin, cid_spotlight.bin).</summary>
	public static int AddLights( GameObject parent, Dictionary<string, byte[]> blobs )
	{
		if ( !AlanWakeMount.Lights )
			return 0;

		var lights = new List<AwLight>();
		try
		{
			if ( blobs.TryGetValue( "cid_pointlight.bin", out var points ) )
				AwLightFile.ReadPointLights( points, lights );
			if ( blobs.TryGetValue( "cid_spotlight.bin", out var spots ) )
				AwLightFile.ReadSpotLights( spots, lights );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] lights: {ex.Message}" );
			return 0;
		}

		if ( lights.Count == 0 )
			return 0;

		var group = new GameObject { Name = "lights", Parent = parent };
		int added = 0;
		foreach ( var light in lights )
		{
			if ( light.Radius <= 0.01f || light.Intensity <= 0f )
				continue;

			float brightness = light.Intensity * AlanWakeMount.LightBrightness;
			var color = new Color( light.Color.x * brightness, light.Color.y * brightness, light.Color.z * brightness );
			float radius = MathF.Min( light.Radius, 300f ) * AwSpace.Scale;

			if ( light.Spot )
			{
				var m = light.Rotation;
				var forward = AwSpace.Direction( m[6], m[7], m[8] ).Normal;
				var up = AwSpace.Direction( m[3], m[4], m[5] ).Normal;
				var go = new GameObject
				{
					Name = "spot_light",
					Parent = group,
					LocalTransform = new Transform( AwSpace.Position( light.Position ), Rotation.LookAt( forward, up ) ),
				};

				float outer = Math.Clamp( MathF.Atan( MathF.Max( light.ConeTangent, 0.01f ) ) * (180f / MathF.PI), 1f, 89f );
				var spot = go.AddComponent<SpotLight>();
				spot.LightColor = color;
				spot.Radius = radius;
				spot.ConeOuter = outer;
				spot.ConeInner = outer * 0.75f;
				spot.Shadows = AlanWakeMount.LightShadows >= 1;
			}
			else
			{
				var go = new GameObject
				{
					Name = "point_light",
					Parent = group,
					LocalTransform = new Transform( AwSpace.Position( light.Position ) ),
				};

				var point = go.AddComponent<PointLight>();
				point.LightColor = color;
				point.Radius = radius;
				point.Shadows = AlanWakeMount.LightShadows >= 2 && light.Shadows;
			}

			added++;
		}

		return added;
	}

	/// <summary>
	/// Adds an <c>AwGrassRenderer</c> carrying the cell's grass layout (from the high detail terrain data); the
	/// renderer grows the clumps around the camera at runtime.
	/// </summary>
	public static int AddGrass( AlanWakeMount host, GameObject parent, Dictionary<string, byte[]> ld, Dictionary<string, byte[]> hd )
	{
		var rendererType = GameComponent( "AwGrassRenderer" );
		if ( rendererType is null )
			return 0;

		if ( !ld.TryGetValue( "cid_terraindata.bin", out var ldTerrain ) || !hd.TryGetValue( "cid_terraindata.bin", out var hdTerrain ) )
			return 0;

		var grass = host.GetGrass();
		if ( grass is null || grass.Clumps.Count == 0 )
			return 0;

		string data;
		int polys;
		try
		{
			var terrain = AwTerrainFile.ReadLowDetail( ldTerrain );
			terrain.ReadHighDetail( hdTerrain );
			data = AwGrassData.Build( terrain, out polys );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] grass: {ex.Message}" );
			return 0;
		}

		if ( data is null )
			return 0;

		var go = new GameObject { Name = "grass", Parent = parent };
		var renderer = go.Components.Create( rendererType );
		Set( renderer, "Clumps", Enumerable.Range( 0, grass.Clumps.Count ).Select( i => Model.Load( AlanWakeMount.GrassClumpModelPath( i ) ) ).ToList() );
		Set( renderer, "Data", data );
		Set( renderer, "Subdivisions", AwTerrainLoader.SubdivisionsFor( true ) );
		Set( renderer, "MaxDistance", AlanWakeMount.GrassDistance * AwSpace.Scale );
		Set( renderer, "Density", AlanWakeMount.GrassDensity );
		return polys;
	}

	/// <summary>
	/// Adds one <c>AwFoliageRenderer</c> per foliage type found in a cell container's cid_foliagedata.bin.
	/// </summary>
	public static int AddFoliage( AlanWakeMount host, GameObject parent, Dictionary<string, byte[]> blobs, string groupName, bool undergrowth )
	{
		var rendererType = GameComponent( "AwFoliageRenderer" );
		if ( rendererType is null )
			return 0;

		if ( !blobs.TryGetValue( "cid_foliagedata.bin", out var data ) )
			return 0;

		List<string> types;
		List<AwFoliagePlacement.Instance> instances;
		try
		{
			(types, instances) = AwFoliagePlacement.Read( data );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] foliage: {ex.Message}" );
			return 0;
		}

		if ( instances.Count == 0 )
			return 0;

		var group = new GameObject { Name = groupName, Parent = parent };
		int added = 0;

		foreach ( var byType in instances.GroupBy( x => x.Type ) )
		{
			if ( byType.Key >= types.Count )
				continue;

			var typePath = types[byType.Key];
			var info = host.GetFoliage( typePath );
			if ( info is null )
				continue;

			var name = System.IO.Path.GetFileNameWithoutExtension( typePath );
			var model = Model.Load( $"mount://alan_wake/foliage/{name}.vmdl" );
			if ( model is null || model.IsError )
				continue;

			float visible = info.VisibleDistance > 0 ? info.VisibleDistance : AlanWakeMount.FoliageMaxDistance;
			float maxDistance, meshDistance;
			Model impostor = null;
			if ( undergrowth )
			{
				maxDistance = MathF.Min( visible, AlanWakeMount.UndergrowthDistance );
				meshDistance = maxDistance;
			}
			else
			{
				maxDistance = MathF.Min( visible, AlanWakeMount.FoliageMaxDistance );
				meshDistance = MathF.Min( AlanWakeMount.FoliageMeshDistance, maxDistance );
				impostor = Model.Load( $"mount://alan_wake/foliage/{name}_impostor.vmdl" );
				if ( impostor is not null && impostor.IsError ) impostor = null;
			}

			var transforms = byType
				.Select( x => new Transform( AwSpace.Position( x.Position ), Rotation.FromYaw( -x.Yaw * (360f / 256f) ) ) )
				.ToList();

			var go = new GameObject { Name = name, Parent = group };
			var renderer = go.Components.Create( rendererType );
			Set( renderer, "Model", model );
			Set( renderer, "Impostor", impostor );
			Set( renderer, "MeshDistance", meshDistance * AwSpace.Scale );
			Set( renderer, "MaxDistance", maxDistance * AwSpace.Scale );
			Set( renderer, "CastShadows", !undergrowth || AlanWakeMount.UndergrowthShadows );
			Set( renderer, "Instances", transforms );
			added += transforms.Count;
		}

		return added;
	}

	static readonly HashSet<string> MissingComponents = new();

	/// <summary>
	/// A component type from the running game's code, by class name. The grass and foliage renderers can't live in
	/// this DLL: prefabs only keep components the game's type library knows (engine and game code, never mount DLLs).
	/// Null, with one warning, when the game doesn't have it.
	/// </summary>
	static TypeDescription GameComponent( string name )
	{
		var type = Game.TypeLibrary?.GetType( typeof( Component ), name );
		if ( type is null )
		{
			lock ( MissingComponents )
			{
				if ( MissingComponents.Add( name ) )
					Log.Warning( $"[AW] this game has no {name} component, cells load without it (copy Code/{name}.cs from the alan_wake project into the game to get it)" );
			}
		}

		return type;
	}

	/// <summary>Sets a property of a component created from <see cref="GameComponent"/>, whose type isn't known here.</summary>
	static void Set( Component component, string property, object value )
	{
		var info = component.GetType().GetProperty( property );
		if ( info is null || !info.CanWrite )
		{
			Log.Warning( $"[AW] {component.GetType().Name} has no settable {property} property" );
			return;
		}

		info.SetValue( component, value );
	}

	static List<AwStaticObject> ReadObjects( Dictionary<string, byte[]> blobs, string name, int recordSize )
	{
		if ( !blobs.TryGetValue( name, out var data ) )
			return new List<AwStaticObject>();

		try
		{
			return AwStaticObjects.Read( data, recordSize );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] {name}: {ex.Message}" );
			return new List<AwStaticObject>();
		}
	}

	static Model LoadModel( string meshPath, bool physics )
	{
		var modelPath = AlanWakeMount.ModelPathForMesh( meshPath, physics );
		if ( modelPath is null )
			return null;

		var model = Model.Load( modelPath );
		return model is null || model.IsError ? null : model;
	}
}
