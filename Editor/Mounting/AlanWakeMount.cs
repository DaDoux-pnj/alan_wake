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
///   worlds/{world}/{level}/terrain_XXX_ZZZ            -> Model     (displaced terrain of one 128 m cell)
///   worlds/{world}/{level}/cell_XXX_ZZZ               -> Prefab    (terrain + static meshes of the cell)
///   worlds/{world}/{level}/{level}_extras             -> Prefab    (level-wide statics, interiors)
/// Use the "aw_place_level" / "aw_place_world" console commands (editor) to drop whole areas in a scene.
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

	[ConVar( "aw_mount_debug_log", ConVarFlags.Saved )]
	public static bool DebugLog { get; set; } = false;

	/// <summary>Terrain subdivisions per terrain quad edge (1, 3, 5 or 15). 15 uses every displacement sample.</summary>
	[ConVar( "aw_terrain_subdivisions", ConVarFlags.Saved )]
	public static int TerrainSubdivisions { get; set; } = 5;

	/// <summary>Distance (inches) at which a cell's terrain switches to its low detail version. 0 disables.</summary>
	[ConVar( "aw_terrain_lod_distance", ConVarFlags.Saved )]
	public static float TerrainLodDistance { get; set; } = 6000;

	/// <summary>Which 5-bit fields of the blend map are the two layer thresholds (0: R,B  1: B,R).</summary>
	[ConVar( "aw_terrain_blend_mode", ConVarFlags.Saved )]
	public static int TerrainBlendMode { get; set; } = 0;

	[ConVar( "aw_terrain_brightness", ConVarFlags.Saved )]
	public static float TerrainBrightness { get; set; } = 1.0f;

	/// <summary>Distance (inches) at which props switch to their LOD1 mesh (when the game provides one).</summary>
	[ConVar( "aw_mesh_lod_distance", ConVarFlags.Saved )]
	public static float MeshLodDistance { get; set; } = 2500;

	[ConVar( "aw_normalmaps", ConVarFlags.Saved )]
	public static bool NormalMaps { get; set; } = true;

	[ConVar( "aw_normalmap_max_size", ConVarFlags.Saved )]
	public static int NormalMapMaxSize { get; set; } = 512;

	[ConVar( "aw_normalmap_flip_green", ConVarFlags.Saved )]
	public static bool NormalMapFlipGreen { get; set; } = true;

	[ConVar( "aw_collision", ConVarFlags.Saved )]
	public static bool Collision { get; set; } = true;

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

		Current = this;
		Archive = AwArchive.Open( Path.Combine( _appDir, "data" ) );
		Log.Info( $"Alan Wake: {Archive.Count} files in archives" );

		int meshes = 0, textures = 0;
		var cells = new Dictionary<string, AwCellInfo>( StringComparer.OrdinalIgnoreCase );
		var levelFiles = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );

		foreach ( var path in Archive.Paths )
		{
			if ( path.EndsWith( ".binmsh", StringComparison.Ordinal ) )
			{
				context.Add( ResourceType.Model, path[..^".binmsh".Length], new AwModelLoader( path ) );
				meshes++;
				continue;
			}

			if ( path.EndsWith( ".tex", StringComparison.Ordinal ) )
			{
				context.Add( ResourceType.Texture, path[..^".tex".Length], new AwTextureLoader( path ) );
				textures++;
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
			context.Add( ResourceType.PrefabFile, cell.PrefabResourcePath, new AwCellPrefabLoader( cell ) );
		}

		int extras = 0;
		foreach ( var (levelKey, files) in levelFiles )
		{
			var parts = levelKey.Split( '/' );
			context.Add( ResourceType.PrefabFile, $"worlds/{parts[0]}/{parts[1]}/{parts[1]}_extras", new AwExtrasPrefabLoader( parts[1], files ) );
			extras++;
		}

		Log.Info( $"Alan Wake: {meshes} meshes, {textures} textures, {CellsByLevel.Sum( x => x.Value.Count )} world cells in {CellsByLevel.Count} levels, {extras} level extras" );

		IsMounted = true;
		return Task.CompletedTask;
	}

	protected override void Shutdown()
	{
		AwMaterials.Clear();
		CellsByLevel.Clear();
		Archive?.Dispose();
		Archive = null;
		if ( Current == this ) Current = null;
	}

	internal static string ModelPathForMesh( string archivePath )
	{
		if ( string.IsNullOrEmpty( archivePath ) || !archivePath.EndsWith( ".binmsh", StringComparison.OrdinalIgnoreCase ) )
			return null;
		return $"mount://alan_wake/{archivePath[..^".binmsh".Length]}.vmdl";
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

	/// <summary>Cell centre in s&amp;box space (height 0).</summary>
	public Vector3 Center => AwSpace.Position( AwSpace.CellOriginMetres( X, Z ) + new Vector3( AwSpace.CellSize * 0.5f, 0, AwSpace.CellSize * 0.5f ) );
}
