using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Sandbox.Mounting;

/// <summary>
/// Mounts Alan Wake (2012 PC release, Steam app 108710) into s&amp;box.
///
/// Everything lives in data/ep999-00N.bin (table of contents) + .rmdp (raw data), see <see cref="AwArchive"/>.
/// Registered resources:
///   objects/...            .binmsh meshes            -> Model     mount://alan_wake/objects/....vmdl
///   textures/...           .tex textures             -> Texture   mount://alan_wake/textures/....vtex
///   audio/...              .fsb sound banks          -> Sound     mount://alan_wake/audio/....vsnd (see AwSoundBank)
///   worlds/{world}/{level}/terrain_XXX_ZZZ            -> Model     (displaced terrain of one 128 m cell)
///   worlds/{world}/{level}/cell_XXX_ZZZ               -> Prefab    (terrain + static meshes of the cell)
///   worlds/{world}/{level}/{level}_extras             -> Prefab    (level-wide statics, interiors)
///   maps/{area}                                       -> Scene     (a playable area with all its parts, see AwMapLoader)
/// Built as a standalone mount (sbox/mount/alan_wake/alan_wake.dll, see alan_wake.mount.csproj), so it has no
/// ConVars or console commands: settings come from alan_wake.json (<see cref="AwSettings"/>), and the
/// "aw_place_level" / "aw_place_world" editor commands live in the alan_wake project's Editor folder, as do the
/// grass and foliage renderer components (its Code folder), which cell prefabs reference by name.
/// </summary>
public class AlanWakeMount : BaseGameMount
{
	public override string Ident => "alan_wake";
	public override string Title => "Alan Wake";
	public override long? SteamAppId => AppId;

	const long AppId = 108710;

	internal static AlanWakeMount Current { get; private set; }
	internal static string InstallDirectory { get; private set; }
	internal AwArchive Archive { get; private set; }

	/// <summary>All world cells, keyed by "world/level".</summary>
	internal readonly Dictionary<string, List<AwCellInfo>> CellsByLevel = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>A level's other world containers (global.bin, interiors...), keyed by "world/level".</summary>
	internal Dictionary<string, List<string>> LevelFiles { get; private set; } = new( StringComparer.OrdinalIgnoreCase );

	// Settings, from alan_wake.json next to the DLL (see AwSettings): the values below are the defaults.

	[AwSetting( "aw_mount_debug_log", "Log textures and terrain that fail to load, and terrain build details." )]
	public static bool DebugLog { get; set; } = false;

	[AwSetting( "aw_terrain_subdivisions", "Terrain subdivisions per terrain quad edge (1, 3, 5 or 15). 15 uses every displacement sample." )]
	public static int TerrainSubdivisions { get; set; } = 5;

	[AwSetting( "aw_terrain_lod_distance", "Distance (inches) at which a cell's terrain switches to its low detail version. 0 disables." )]
	public static float TerrainLodDistance { get; set; } = 6000;

	[AwSetting( "aw_terrain_brightness", "Multiplier on the terrain colour." )]
	public static float TerrainBrightness { get; set; } = 1.0f;

	[AwSetting( "aw_mesh_lod_distance", "Distance (inches) at which props switch to their LOD1 mesh (when the game provides one)." )]
	public static float MeshLodDistance { get; set; } = 2500;

	[AwSetting( "aw_normalmaps", "Use the game's normal maps." )]
	public static bool NormalMaps { get; set; } = true;

	[AwSetting( "aw_normalmap_max_size", "Normal maps bigger than this (pixels) are scaled down." )]
	public static int NormalMapMaxSize { get; set; } = 512;

	[AwSetting( "aw_normalmap_flip_green", "Flip the green channel of the game's normal maps." )]
	public static bool NormalMapFlipGreen { get; set; } = true;

	[AwSetting( "aw_specular_maps", "Vary model roughness per pixel with the game's specular maps (matte where they're dark), like the game's highlights." )]
	public static bool SpecularMaps { get; set; } = true;

	[AwSetting( "aw_glass_refraction", "Windows use s&box's refractive glass (true) or its cheaper alpha blended glass (false)." )]
	public static bool GlassRefraction { get; set; } = true;

	[AwSetting( "aw_collision", "Give the terrain and static meshes collision." )]
	public static bool Collision { get; set; } = true;

	[AwSetting( "aw_lights", "Place the game's point and spot lights." )]
	public static bool Lights { get; set; } = true;

	[AwSetting( "aw_light_brightness", "Multiplier from the game's light intensity to s&box light colour brightness." )]
	public static float LightBrightness { get; set; } = 2.0f;

	[AwSetting( "aw_light_shadows", "0 = no light shadows, 1 = spot lights only, 2 = spot lights and point lights the game marks as shadowed." )]
	public static int LightShadows { get; set; } = 1;

	[AwSetting( "aw_dynamic_objects", "Place the game's dynamic objects (doors, barrels, chairs...)." )]
	public static bool DynamicObjects { get; set; } = true;

	[AwSetting( "aw_dynamic_physics", "Give small loose dynamic objects (barrels, chairs, cans...) real physics. Doors, gates, poles stay put." )]
	public static bool DynamicPhysics { get; set; } = true;

	[AwSetting( "aw_foliage", "Place trees and undergrowth (needs the AwFoliageRenderer component in the game)." )]
	public static bool Foliage { get; set; } = true;

	[AwSetting( "aw_foliage_mesh_distance", "Metres. Trees closer than this draw their full mesh, further ones their billboard." )]
	public static float FoliageMeshDistance { get; set; } = 240;

	[AwSetting( "aw_foliage_max_distance", "Metres. Trees beyond this are not drawn (the game itself goes up to ~2200 m)." )]
	public static float FoliageMaxDistance { get; set; } = 2000;

	[AwSetting( "aw_foliage_undergrowth_distance", "Metres. Ferns, shrubs and grass tufts fade out at this distance (or earlier if the game says so)." )]
	public static float UndergrowthDistance { get; set; } = 60;

	[AwSetting( "aw_foliage_impostor_brightness", "Brightness of distant foliage billboards (impostors) relative to their atlas colour." )]
	public static float FoliageImpostorBrightness { get; set; } = 0.85f;

	[AwSetting( "aw_foliage_undergrowth_shadows", "Let undergrowth cast shadows." )]
	public static bool UndergrowthShadows { get; set; } = false;

	[AwSetting( "aw_grass", "Grow the game's grass (needs the AwGrassRenderer component in the game)." )]
	public static bool Grass { get; set; } = true;

	[AwSetting( "aw_grass_distance", "Metres. Grass grows around the camera up to this distance (the game uses 15 to 25)." )]
	public static float GrassDistance { get; set; } = 100;

	[AwSetting( "aw_grass_density", "Multiplier on the game's grass density." )]
	public static float GrassDensity { get; set; } = 1;

	[AwSetting( "aw_grass_wind", "Inches the grass tips sway in the wind (0 = still)." )]
	public static float GrassWind { get; set; } = 3;

	[AwSetting( "aw_sounds", "Give maps the game's ambient sounds (birds, rivers, creaks, hums...)." )]
	public static bool Sounds { get; set; } = true;

	[AwSetting( "aw_sound_daytime", "Prefer daytime ambience: maps that have some leave out their night sounds (owls, crickets...). Off prefers night." )]
	public static bool SoundDaytime { get; set; } = true;

	[AwSetting( "aw_sound_volume", "Multiplier on the game's sound volumes." )]
	public static float SoundVolume { get; set; } = 1;

	internal const string GrassPath = "grass/grass.bingrs";

	/// <summary>The s&amp;box sound of a sound bank, e.g. audio/ambience/x.fsb -&gt; mount://alan_wake/audio/ambience/x.vsnd.</summary>
	internal static string SoundPath( string archivePath ) => $"mount://alan_wake/{archivePath[..^".fsb".Length]}.vsnd";

	readonly object _soundLock = new();
	AwSoundDatabase _sounds;
	readonly Dictionary<string, List<AwSoundInstances.Instance>> _episodeSounds = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>The game's sound definitions (cached), or null if they can't be read.</summary>
	internal AwSoundDatabase GetSoundDatabase()
	{
		lock ( _soundLock )
		{
			if ( _sounds is not null )
				return _sounds;

			try
			{
				_sounds = AwSoundDatabase.Load( Archive );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[AW] sound database: {ex.Message}" );
				_sounds = new AwSoundDatabase();
			}

			return _sounds;
		}
	}

	/// <summary>
	/// Sound emitters the episodes' task files place in a world (worlds/{world}/episodes/**), cached. The game's scripts
	/// switch them on as the story goes; a map plays the ones inside it all the time.
	/// </summary>
	internal List<AwSoundInstances.Instance> GetEpisodeSounds( string world )
	{
		lock ( _soundLock )
		{
			if ( _episodeSounds.TryGetValue( world, out var cached ) )
				return cached;
		}

		var instances = new List<AwSoundInstances.Instance>();
		var prefix = $"worlds/{world}/episodes/";
		foreach ( var path in Archive.Paths )
		{
			if ( !path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) || !path.EndsWith( ".bin", StringComparison.Ordinal ) )
				continue;

			try
			{
				AwSoundInstances.Read( AwCellFile.Read( Archive.Read( path ) ), instances );
			}
			catch ( Exception ex )
			{
				if ( DebugLog ) Log.Warning( $"[AW] episode sounds '{path}': {ex.Message}" );
			}
		}

		lock ( _soundLock ) _episodeSounds[world] = instances;
		return instances;
	}

	internal static string GrassClumpModelPath( int index ) => $"mount://alan_wake/grass/clump_{index:D2}.vmdl";

	readonly object _grassLock = new();
	AwGrassFile _grass;
	bool _grassLoaded;

	/// <summary>The game's grass clumps (cached), or null if missing/broken.</summary>
	internal AwGrassFile GetGrass()
	{
		lock ( _grassLock )
		{
			if ( _grassLoaded )
				return _grass;
			_grassLoaded = true;

			try
			{
				var bytes = Archive.TryRead( GrassPath );
				if ( bytes is not null )
					_grass = AwGrassFile.Parse( bytes );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[AW] grass: {ex.Message}" );
			}

			return _grass;
		}
	}

	readonly Dictionary<string, AwFoliageFile> _foliage = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Parsed foliage type (cached), or null if missing/broken.</summary>
	internal AwFoliageFile GetFoliage( string archivePath )
	{
		archivePath = AwArchive.NormalizePath( archivePath );
		lock ( _foliage )
		{
			if ( _foliage.TryGetValue( archivePath, out var cached ) )
				return cached;
		}

		AwFoliageFile file = null;
		try
		{
			var bytes = Archive.TryRead( archivePath );
			if ( bytes is not null )
				file = AwFoliageFile.Parse( bytes );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] foliage type '{archivePath}': {ex.Message}" );
		}

		lock ( _foliage ) _foliage[archivePath] = file;
		return file;
	}

	string _appDir;

	static readonly Regex CellRegex = new( @"^worlds/([^/]+)/levels/([^/]+)/(hd|ld)(\d+)_(\d+)\.bin$", RegexOptions.CultureInvariant );
	static readonly Regex LevelFileRegex = new( @"^worlds/([^/]+)/levels/([^/]+)/([^/]+)\.bin$", RegexOptions.CultureInvariant );

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( AppId ) )
			return;

		_appDir = context.GetAppDirectory( AppId );
		InstallDirectory = _appDir;
		IsInstalled = System.IO.Directory.Exists( Path.Combine( _appDir, "data" ) );
	}

	protected override Task Mount( MountContext context )
	{
		if ( string.IsNullOrEmpty( _appDir ) )
			return Task.CompletedTask;

		AwSettings.Load( Log );

		Current = this;
		Archive = AwArchive.Open( Path.Combine( _appDir, "data" ) );
		Log.Info( $"Alan Wake: {Archive.Count} files in archives" );

		int meshes = 0, textures = 0, soundBanks = 0;
		var cells = new Dictionary<string, AwCellInfo>( StringComparer.OrdinalIgnoreCase );
		var levelFiles = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );

		foreach ( var path in Archive.Paths )
		{
			if ( path.EndsWith( ".binmsh", StringComparison.Ordinal ) )
			{
				context.Add( ResourceType.Model, path[..^".binmsh".Length], new AwModelLoader( path ) );
				context.Add( ResourceType.Model, path[..^".binmsh".Length] + "_physics", new AwModelLoader( path, physics: true ) );
				meshes++;
				continue;
			}

			if ( path.EndsWith( ".tex", StringComparison.Ordinal ) )
			{
				context.Add( ResourceType.Texture, path[..^".tex".Length], new AwTextureLoader( path ) );
				textures++;
				continue;
			}

			if ( path.EndsWith( ".binfol", StringComparison.Ordinal ) )
			{
				var name = path[..^".binfol".Length];
				context.Add( ResourceType.Model, name, new AwFoliageModelLoader( path, false ) );
				context.Add( ResourceType.Model, name + "_impostor", new AwFoliageModelLoader( path, true ) );
				continue;
			}

			if ( path == GrassPath )
			{
				var header = Archive.ReadPrefix( path, 8 );
				int clumps = header is { Length: >= 8 } ? Math.Clamp( BitConverter.ToInt32( header, 4 ), 0, 64 ) : 0;
				for ( int i = 0; i < clumps; i++ )
					context.Add( ResourceType.Model, $"grass/clump_{i:D2}", new AwGrassClumpLoader( i ) );
				continue;
			}

			if ( path.StartsWith( "audio/", StringComparison.Ordinal ) && path.EndsWith( ".fsb", StringComparison.Ordinal ) )
			{
				context.Add( ResourceType.Sound, path[..^".fsb".Length], new AwSoundLoader( path ) );
				soundBanks++;
				continue;
			}

			if ( !path.StartsWith( "worlds/", StringComparison.Ordinal ) || !path.EndsWith( ".bin", StringComparison.Ordinal ) )
				continue;

			var m = CellRegex.Match( path );
			if ( m.Success )
			{
				string world = m.Groups[1].Value, level = m.Groups[2].Value;
				int x = int.Parse( m.Groups[4].Value ), z = int.Parse( m.Groups[5].Value );
				var key = $"{world}/{level}/{x}/{z}";
				if ( !cells.TryGetValue( key, out var cell ) )
					cells[key] = cell = new AwCellInfo( world, level, x, z );

				if ( m.Groups[3].Value == "hd" ) cell.HighDetailPath = path;
				else cell.LowDetailPath = path;
				continue;
			}

			var lf = LevelFileRegex.Match( path );
			if ( lf.Success )
			{
				var name = lf.Groups[3].Value;
				if ( name is "persistent" ) continue;
				var key = $"{lf.Groups[1].Value}/{lf.Groups[2].Value}";
				if ( !levelFiles.TryGetValue( key, out var list ) ) levelFiles[key] = list = new List<string>();
				list.Add( path );
			}
		}

		foreach ( var cell in cells.Values.Where( c => c.LowDetailPath is not null ) )
		{
			var levelKey = $"{cell.World}/{cell.Level}";
			if ( !CellsByLevel.TryGetValue( levelKey, out var list ) )
				CellsByLevel[levelKey] = list = new List<AwCellInfo>();
			list.Add( cell );

			context.Add( ResourceType.Model, cell.TerrainResourcePath, new AwTerrainLoader( cell ) );

			// The placement commands (alan_wake project, Editor/AwWorldCommands.cs) list playable cells by this tag.
			var prefab = new AwCellPrefabLoader( cell );
			if ( cell.HighDetailPath is not null )
				prefab.Tags.Add( "aw_high_detail" );
			context.Add( ResourceType.PrefabFile, cell.PrefabResourcePath, prefab );
		}

		LevelFiles = levelFiles;
		int extras = 0;
		foreach ( var (levelKey, files) in levelFiles )
		{
			var parts = levelKey.Split( '/' );
			context.Add( ResourceType.PrefabFile, $"worlds/{parts[0]}/{parts[1]}/{parts[1]}_extras", new AwExtrasPrefabLoader( parts[1], files ) );
			extras++;
		}

		// One map per playable area, with every part of it ("brightfalls_part_1" to "_4" make Bright Falls). Areas
		// without high detail cells are distant scenery the player can't walk on, so they get no map.
		int maps = 0;
		foreach ( var area in CellsByLevel.Values.GroupBy( cells => (cells[0].World, Area: AwMapLoader.AreaOf( cells[0].Level )) ) )
		{
			var areaCells = area.SelectMany( x => x ).ToList();
			if ( !areaCells.Any( c => c.HighDetailPath is not null ) )
				continue;

			var levels = area.Select( x => x[0].Level ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToList();
			context.Add( ResourceType.Scene, $"maps/{AwMapLoader.MapName( area.Key.World, area.Key.Area )}", new AwMapLoader( area.Key.World, levels, areaCells ) );
			maps++;
		}

		Log.Info( $"Alan Wake: {meshes} meshes, {textures} textures, {soundBanks} sounds, {CellsByLevel.Sum( x => x.Value.Count )} world cells in {CellsByLevel.Count} levels, {extras} level extras, {maps} maps" );

		IsMounted = true;
		return Task.CompletedTask;
	}

	protected override void Shutdown()
	{
		AwMaterials.Clear();
		CellsByLevel.Clear();
		LevelFiles = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );
		lock ( _foliage ) _foliage.Clear();
		lock ( _grassLock )
		{
			_grass = null;
			_grassLoaded = false;
		}
		lock ( _soundLock )
		{
			_sounds = null;
			_episodeSounds.Clear();
		}
		Archive?.Dispose();
		Archive = null;
		if ( Current == this ) Current = null;
	}

	internal static string ModelPathForMesh( string archivePath, bool physics = false )
	{
		if ( string.IsNullOrEmpty( archivePath ) || !archivePath.EndsWith( ".binmsh", StringComparison.OrdinalIgnoreCase ) )
			return null;
		return $"mount://alan_wake/{archivePath[..^".binmsh".Length]}{(physics ? "_physics" : "")}.vmdl";
	}
}

/// <summary>One 128 m world cell. Low detail data always exists, high detail only in playable areas.</summary>
sealed class AwCellInfo
{
	public AwCellInfo( string world, string level, int x, int z )
	{
		World = world;
		Level = level;
		X = x;
		Z = z;
	}

	public string World { get; }
	public string Level { get; }
	public int X { get; }
	public int Z { get; }
	public string HighDetailPath { get; set; }
	public string LowDetailPath { get; set; }

	public string Name => $"{X:D3}_{Z:D3}";
	public string TerrainResourcePath => $"worlds/{World}/{Level}/terrain_{Name}";
	public string PrefabResourcePath => $"worlds/{World}/{Level}/cell_{Name}";
	public string TerrainModelPath => $"mount://alan_wake/{TerrainResourcePath}.vmdl";
	public string PrefabPath => $"mount://alan_wake/{PrefabResourcePath}.prefab";

	/// <summary>Cell centre in Alan Wake space (metres, height 0).</summary>
	public Vector3 CenterMetres => AwSpace.CellOriginMetres( X, Z ) + new Vector3( AwSpace.CellSize * 0.5f, 0, AwSpace.CellSize * 0.5f );

	/// <summary>Cell centre in s&amp;box space (height 0).</summary>
	public Vector3 Center => AwSpace.Position( CenterMetres );
}
