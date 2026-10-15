using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sandbox.Mounting;

/// <summary>
/// Editor console commands to drop Alan Wake world areas into the active scene.
///   aw_list_levels                                   lists worlds/levels and their cell counts
///   aw_place_level gameworld brightfalls_part_4      places one level (comma separate several levels)
///   aw_place_world gameworld                         places every level of a world (heavy!)
/// Levels are re-centred so the placed area sits around the scene origin; levels placed by the same
/// command share one offset so they line up.
/// The mount itself is a standalone DLL (sbox/mount/alan_wake, built from Mount/), which can't register console
/// commands, so they live here and find the cells through the mount's list of prefabs.
/// </summary>
static class AwWorldCommands
{
	/// <summary>Cell prefabs: worlds/{world}/{level}/cell_XXX_ZZZ, tagged "aw_high_detail" in playable areas.</summary>
	static readonly Regex CellPrefab = new( @"^worlds/([^/]+)/([^/]+)/cell_(\d+)_(\d+)\.prefab$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	sealed class Cell
	{
		public int X;
		public int Z;
		public string PrefabPath;
		public bool HighDetail;

		/// <summary>
		/// Cell centre in s&amp;box space (height 0), as AwCellInfo.Center in the mount: cells are 128 m wide, cell
		/// (40, 40) starts at the origin and the game's z axis is s&amp;box's y.
		/// </summary>
		public Vector3 Center => new Vector3( (X - 40 + 0.5f) * 128f, (Z - 40 + 0.5f) * 128f, 0 ) * 39.3701f;
	}

	[ConCmd( "aw_list_levels", Help = "List Alan Wake worlds/levels that can be placed with aw_place_level." )]
	public static void ListLevels()
	{
		var cellsByLevel = CellsByLevel();
		if ( cellsByLevel is null )
			return;

		foreach ( var group in cellsByLevel.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase ) )
		{
			int hd = group.Value.Count( c => c.HighDetail );
			Log.Info( $"{group.Key.Replace( '/', ' ' ),-45} {group.Value.Count,4} cells ({hd} high detail)" );
		}
	}

	[ConCmd( "aw_place_level", Help = "Place Alan Wake level(s) into the editor scene: aw_place_level <world> <level[,level2,...]>" )]
	public static void PlaceLevel( string world, string levels )
	{
		var names = (levels ?? "").Split( new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries );
		Place( world, names, $"Alan Wake - {string.Join( ", ", names )}" );
	}

	[ConCmd( "aw_place_world", Help = "Place every level of an Alan Wake world (gameworld, dlc1_world, dlc2_world). Very heavy." )]
	public static void PlaceWorld( string world )
	{
		var cellsByLevel = CellsByLevel();
		if ( cellsByLevel is null )
			return;

		var names = cellsByLevel.Keys
			.Where( k => k.StartsWith( world + "/", StringComparison.OrdinalIgnoreCase ) )
			.Select( k => k[(world.Length + 1)..] )
			.ToArray();
		Place( world, names, $"Alan Wake - {world}" );
	}

	/// <summary>The mounted game's cells keyed by "world/level", or null (with a warning) when it isn't mounted.</summary>
	static Dictionary<string, List<Cell>> CellsByLevel()
	{
		var mount = Directory.Get( "alan_wake" );
		if ( mount is null || !mount.IsMounted )
		{
			Log.Warning( "Alan Wake is not mounted. Install the mount (see README.md), restart s&box and enable 'alan_wake' first." );
			return null;
		}

		var cellsByLevel = new Dictionary<string, List<Cell>>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in mount.GetAll( ResourceType.PrefabFile ) )
		{
			var m = CellPrefab.Match( entry.RelativePath ?? "" );
			if ( !m.Success )
				continue;

			var key = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
			if ( !cellsByLevel.TryGetValue( key, out var list ) )
				cellsByLevel[key] = list = new List<Cell>();

			list.Add( new Cell
			{
				X = int.Parse( m.Groups[3].Value ),
				Z = int.Parse( m.Groups[4].Value ),
				PrefabPath = entry.Path,
				HighDetail = entry.Tags.Contains( "aw_high_detail" ),
			} );
		}

		return cellsByLevel;
	}

	static void Place( string world, string[] levels, string rootName )
	{
		var cellsByLevel = CellsByLevel();
		if ( cellsByLevel is null )
			return;

		var session = SceneEditorSession.Active;
		if ( session?.Scene is null ) { Log.Warning( "No active editor scene." ); return; }

		var cells = levels
			.SelectMany( l => cellsByLevel.TryGetValue( $"{world}/{l}", out var list ) ? list : Enumerable.Empty<Cell>() )
			.ToList();
		if ( cells.Count == 0 )
		{
			Log.Warning( $"No cells found for {world}: {string.Join( ", ", levels )}. Use aw_list_levels." );
			return;
		}

		var center = cells.Aggregate( Vector3.Zero, ( sum, c ) => sum + c.Center ) / cells.Count;
		center = center.WithZ( 0 );

		using var sceneScope = session.Scene.Push();
		using ( session.UndoScope( $"Place {rootName}" ).WithGameObjectCreations().Push() )
		{
			var root = session.Scene.CreateObject();
			root.Name = rootName;
			root.WorldPosition = -center;

			int placed = 0;
			foreach ( var level in levels )
			{
				var levelCells = cellsByLevel.TryGetValue( $"{world}/{level}", out var list ) ? list : null;
				if ( levelCells is null ) continue;

				var levelRoot = new GameObject { Name = level, Parent = root };

				foreach ( var cell in levelCells.OrderBy( c => c.X ).ThenBy( c => c.Z ) )
				{
					if ( Instantiate( cell.PrefabPath, levelRoot, $"cell_{cell.X:D3}_{cell.Z:D3}" ) )
						placed++;
				}

				Instantiate( $"mount://alan_wake/worlds/{world}/{level}/{level}_extras.prefab", levelRoot, "extras" );
			}

			session.Selection.Set( root );
			Log.Info( $"[AW] placed {placed} cells under '{rootName}' (offset {-center})" );
		}
	}

	static bool Instantiate( string prefabPath, GameObject parent, string name )
	{
		var prefab = GameObject.GetPrefab( prefabPath );
		if ( !prefab.IsValid() )
			return false;

		var instance = prefab.Clone();
		instance.Name = name;
		instance.SetParent( parent, false );
		return true;
	}
}
