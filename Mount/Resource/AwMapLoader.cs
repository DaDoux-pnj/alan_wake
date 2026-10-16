using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Sandbox.Mounting;

/// <summary>
/// One playable area as a map for s&amp;box's map picker (mount://alan_wake/maps/{name}.scene). The game streams an
/// area in parts ("brightfalls_part_1" to "_4", "cauldronlake_01" to "_09"); the map holds all of them: every cell
/// and the parts' extras as prefab instances, moved so the playable cells sit around the origin (heights kept), the
/// sun and sky of the alan_wake project's scene, the area's ambient sounds, and a spawn point on open ground near the
/// middle. With <paramref name="spawnArea"/> the map can hold more than one area (open_world: the whole game world)
/// while centring on and spawning in that one.
/// Games load it through MapInstance; grass and trees only show in games that have the grass and foliage renderer
/// components (see <see cref="AwStaticPlacer"/>).
/// </summary>
sealed class AwMapLoader( string world, List<string> levels, List<AwCellInfo> cells, string spawnArea = null ) : SceneLoader<AlanWakeMount>
{
	static readonly Regex AreaSuffix = new( @"^(.+?)(?:_part_?\d+|_?\d+)$", RegexOptions.CultureInvariant );

	/// <summary>The area a level belongs to: "brightfalls_part_4" -> "brightfalls", "cauldronlake_01" -> "cauldronlake".</summary>
	public static string AreaOf( string level )
	{
		var m = AreaSuffix.Match( level );
		return m.Success ? m.Groups[1].Value : level;
	}

	/// <summary>The map name of an area, prefixed with its DLC outside the main game world ("dlc1_brightfalls").</summary>
	public static string MapName( string world, string area )
		=> world.Equals( "gameworld", StringComparison.OrdinalIgnoreCase ) ? area : $"{world.Replace( "_world", "" )}_{area}";

	protected override Task<object> LoadAsync()
	{
		// Build the cell prefabs first: BuildScene runs inside a callback batch, which would hold back the prefab
		// builder's component callbacks until after its temporary scenes are gone.
		foreach ( var level in levels )
		{
			foreach ( var cell in CellsOf( level ) )
				GameObject.GetPrefab( cell.PrefabPath );
			if ( HasExtras( level ) )
				GameObject.GetPrefab( ExtrasPath( level ) );
		}

		return base.LoadAsync();
	}

	protected override void BuildScene()
	{
		var playable = cells.Where( c => c.HighDetailPath is not null ).ToList();
		var spawnCells = playable.Where( c => string.Equals( AreaOf( c.Level ), spawnArea, StringComparison.OrdinalIgnoreCase ) ).ToList();
		if ( spawnCells.Count > 0 )
			playable = spawnCells;

		var middle = playable.Aggregate( Vector3.Zero, ( sum, c ) => sum + c.CenterMetres ) / playable.Count;
		var offset = -AwSpace.Position( middle );

		var root = new GameObject { Name = Name, WorldPosition = offset };
		var cellObjects = new Dictionary<AwCellInfo, GameObject>();
		foreach ( var level in levels )
		{
			var levelRoot = new GameObject { Name = level, Parent = root };
			foreach ( var cell in CellsOf( level ) )
			{
				var instance = Instantiate( cell.PrefabPath, levelRoot, $"cell_{cell.Name}" );
				if ( instance is not null )
					cellObjects[cell] = instance;
			}

			if ( HasExtras( level ) )
				Instantiate( ExtrasPath( level ), levelRoot, "extras" );
		}

		AddSunAndSky();
		AddSounds( root );
		AddSpawn( playable, middle, offset, cellObjects );
		AddDetailStreamer( root );
	}

	/// <summary>
	/// Streams the cells' detail groups (props, physics props, lights, undergrowth, grass, sounds) by distance from the
	/// camera, as the game streams its high detail cells: the AwDetailStreamer component of the alan_wake project's Code
	/// folder (aw_detail_distance). Games without it keep everything on.
	/// </summary>
	static void AddDetailStreamer( GameObject root )
	{
		if ( AlanWakeMount.DetailDistance <= 0 || AwStaticPlacer.GameComponent( "AwDetailStreamer" ) is not { } type )
			return;

		var streamer = root.Components.Create( type );
		AwStaticPlacer.Set( streamer, "Distance", AlanWakeMount.DetailDistance * AwSpace.Scale );
	}

	/// <summary>
	/// Areas the game also visits in daylight (it has day ambience tracks for them), so day sounds win there; the whole
	/// world spawns in Bright Falls by day.
	/// </summary>
	static readonly HashSet<string> DaytimeMaps = new( StringComparer.OrdinalIgnoreCase ) { "brightfalls", "level2", "level_16", "open_world" };

	/// <summary>
	/// The ambience of the whole map: the sound emitters of its cells, its level files and the episodes (whose scripts
	/// run them here), filtered by <see cref="AwSoundPlacer.Collect"/>. Daytime is preferred (aw_sound_daytime): a map
	/// with day ambience (a daylight area, or a fair share of day sounds) drops its night sounds; a map with night
	/// ambience only keeps it. The game's 2D area loops (ambient_tracks) are left out: too loud, and out of place.
	/// </summary>
	void AddSounds( GameObject root )
	{
		if ( !AlanWakeMount.Sounds || Host.GetSoundDatabase() is not { } db )
			return;

		var instances = new List<AwSoundInstances.Instance>();
		foreach ( var cell in cells )
		{
			foreach ( var container in new[] { cell.HighDetailPath, cell.LowDetailPath } )
			{
				if ( AwStaticPlacer.ReadContainer( Host, container ) is { } blobs )
					AwSoundInstances.Read( blobs, instances );
			}
		}

		foreach ( var level in levels )
		{
			foreach ( var file in Host.LevelFiles.GetValueOrDefault( $"{world}/{level}" ) ?? new List<string>() )
			{
				if ( AwStaticPlacer.ReadContainer( Host, file ) is { } blobs )
					AwSoundInstances.Read( blobs, instances );
			}
		}

		var inside = new HashSet<(int, int)>( cells.Select( c => (c.X, c.Z) ) );
		instances.AddRange( Host.GetEpisodeSounds( world ).Where( x => inside.Contains( CellOf( x.Position ) ) ) );

		var emitters = AwSoundPlacer.Collect( db, instances.Where( x => !IsAmbientTrack( db, x ) ) );
		int day = emitters.Count( e => e.Tag == "day" ), night = emitters.Count( e => e.Tag == "night" );
		bool hasDay = DaytimeMaps.Contains( Name ) || (day > 0 && day * 10 >= day + night);
		bool daytime = AlanWakeMount.SoundDaytime ? hasDay : night == 0;
		string dropped = daytime ? "night" : "day";

		int placed = AwSoundPlacer.Place( root, "sounds", emitters.Where( e => e.Tag != dropped ) );
		Log.Info( $"[AW] map {Name}: {placed} ambient sounds ({(daytime ? "day" : "night")})" );
	}

	/// <summary>The rare placed emitter of one of the game's 2D area loops (ambient_tracks), left out like the beds.</summary>
	static bool IsAmbientTrack( AwSoundDatabase db, AwSoundInstances.Instance instance )
		=> instance.Class == 18 && db.Sounds.TryGetValue( instance.Gid, out var sound )
			&& (sound.Name ?? "").Contains( "ambient_tracks", StringComparison.OrdinalIgnoreCase );

	static (int, int) CellOf( Vector3 metres )
		=> ((int)MathF.Floor( metres.x / AwSpace.CellSize ) + AwSpace.CellBias, (int)MathF.Floor( metres.z / AwSpace.CellSize ) + AwSpace.CellBias);

	IEnumerable<AwCellInfo> CellsOf( string level ) => cells.Where( c => c.Level == level ).OrderBy( c => c.X ).ThenBy( c => c.Z );

	string ExtrasPath( string level ) => $"mount://alan_wake/worlds/{world}/{level}/{level}_extras.prefab";

	bool HasExtras( string level ) => Host.GetByPath( ExtrasPath( level ) ) is not null;

	static GameObject Instantiate( string prefabPath, GameObject parent, string name )
	{
		var prefab = GameObject.GetPrefab( prefabPath );
		if ( !prefab.IsValid() )
			return null;

		var instance = prefab.Clone();
		instance.Name = name;
		instance.SetParent( parent, false );
		return instance;
	}

	/// <summary>A clear day sun (pitch 45, yaw 15) and sky, for every map (the game's own lighting isn't mounted).</summary>
	static void AddSunAndSky()
	{
		var sun = new GameObject { Name = "Sun", WorldRotation = Rotation.From( new Angles( 45, 15, 0 ) ) };
		var light = sun.AddComponent<DirectionalLight>();
		light.LightColor = Color.White;
		light.SkyColor = new Color( 0.561f, 0.627f, 0.690f, 1.0f );
		light.Shadows = true;

		var sky = new GameObject { Name = "Sky" };
		var skybox = sky.AddComponent<SkyBox2D>();
		skybox.SkyMaterial = Material.Load( "materials/skybox/skybox_day_01.vmat" );
		skybox.SkyIndirectLighting = true;
	}

	/// <summary>
	/// A spawn point on open ground near the middle of the playable cells, facing it: the closest terrain poly whose
	/// centre isn't steep and where nothing placed in the cells (houses, rocks, cars...) overlaps a standing player.
	/// </summary>
	void AddSpawn( List<AwCellInfo> playable, Vector3 middle, Vector3 offset, Dictionary<AwCellInfo, GameObject> cellObjects )
	{
		var target = AwSpace.Position( middle ) + offset;
		Vector3? best = null, closest = null;
		float bestDistance = float.MaxValue, closestDistance = float.MaxValue;

		foreach ( var cell in playable.OrderBy( c => (c.CenterMetres - middle).Length ).Take( 4 ) )
		{
			var terrain = ReadTerrain( cell );
			if ( terrain is null )
				continue;

			var obstacles = Obstacles( cellObjects.Where( x => Math.Abs( x.Key.X - cell.X ) <= 1 && Math.Abs( x.Key.Z - cell.Z ) <= 1 ).Select( x => x.Value ) );

			for ( int i = 0; i < terrain.Polys.Length; i++ )
			{
				var grid = AwTerrainLoader.SurfaceGrid( terrain, i );
				if ( grid is null )
					continue;

				// The player stands on the middle of the poly's grid (rows and columns 6 to 9).
				float top = float.MinValue;
				for ( int r = 6; r <= 9; r++ )
				for ( int c = 6; c <= 9; c++ )
					top = MathF.Max( top, grid[r * 16 + c].y );

				var centre = (grid[7 * 16 + 7] + grid[7 * 16 + 8] + grid[8 * 16 + 7] + grid[8 * 16 + 8]) * 0.25f;
				var feet = AwSpace.Position( centre.WithY( top + 0.1f ) ) + offset;
				float distance = (feet - target).WithZ( 0 ).Length;

				if ( distance < closestDistance )
				{
					closest = feet;
					closestDistance = distance;
				}

				if ( distance >= bestDistance )
					continue;

				// Alan Wake space is y up: a normal with less than 0.8 up (about 37 degrees of slope) is too steep.
				var normal = Vector3.Cross( grid[7 * 16 + 9] - grid[7 * 16 + 6], grid[9 * 16 + 7] - grid[6 * 16 + 7] ).Normal;
				if ( MathF.Abs( normal.y ) < 0.8f || Blocked( feet, obstacles ) )
					continue;

				best = feet;
				bestDistance = distance;
			}
		}

		var position = best ?? closest ?? target;
		var facing = (target - position).WithZ( 0 );
		var spawn = new GameObject
		{
			Name = "Spawn",
			WorldPosition = position,
			WorldRotation = facing.Length > 100 ? Rotation.LookAt( facing.Normal ) : Rotation.Identity,
		};
		spawn.AddComponent<SpawnPoint>();
		spawn.Tags.Add( "spawn" );

		if ( best is null )
			Log.Warning( $"[AW] map {Name}: no open ground found near the middle, spawning on the closest terrain" );
	}

	AwTerrainFile ReadTerrain( AwCellInfo cell )
	{
		try
		{
			var ld = AwStaticPlacer.ReadContainer( Host, cell.LowDetailPath );
			if ( ld is null || !ld.TryGetValue( "cid_terraindata.bin", out var ldTerrain ) )
				return null;

			var terrain = AwTerrainFile.ReadLowDetail( ldTerrain );
			var hd = AwStaticPlacer.ReadContainer( Host, cell.HighDetailPath );
			if ( hd is not null && hd.TryGetValue( "cid_terraindata.bin", out var hdTerrain ) )
				terrain.ReadHighDetail( hdTerrain );
			return terrain;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] map {Name}: terrain of cell {cell.Name}: {ex.Message}" );
			return null;
		}
	}

	/// <summary>World bounds of everything placed in these cells, except their terrain and anything over 100 m (water, landscape).</summary>
	static List<BBox> Obstacles( IEnumerable<GameObject> cellObjects )
	{
		var boxes = new List<BBox>();
		foreach ( var cell in cellObjects )
		foreach ( var renderer in cell.GetComponentsInChildren<ModelRenderer>( true ) )
		{
			if ( renderer.Model is null || renderer.GameObject.Name == "terrain" )
				continue;

			var box = renderer.Model.Bounds.Transform( renderer.WorldTransform );
			if ( box.Size.x < 4000 && box.Size.y < 4000 )
				boxes.Add( box );
		}

		return boxes;
	}

	/// <summary>Whether a standing player (a 48 x 48 x 80 inch box on these feet) overlaps any obstacle.</summary>
	static bool Blocked( Vector3 feet, List<BBox> obstacles )
	{
		var mins = feet + new Vector3( -24, -24, 0 );
		var maxs = feet + new Vector3( 24, 24, 80 );
		foreach ( var box in obstacles )
		{
			if ( box.Mins.x < maxs.x && box.Maxs.x > mins.x && box.Mins.y < maxs.y && box.Maxs.y > mins.y && box.Mins.z < maxs.z && box.Maxs.z > mins.z )
				return true;
		}

		return false;
	}
}
