using System;
using System.Linq;

namespace Sandbox.Mounting;

/// <summary>
/// Editor console commands to drop Alan Wake world areas into the active scene.
///   aw_list_levels                                   lists worlds/levels and their cell counts
///   aw_place_level gameworld brightfalls_part_4      places one level (comma separate several levels)
///   aw_place_world gameworld                         places every level of a world (heavy!)
/// Levels are re-centred so the placed area sits around the scene origin; levels placed by the same
/// command share one offset so they line up.
/// </summary>
static class AwWorldCommands
{
	[ConCmd( "aw_list_levels", Help = "List Alan Wake worlds/levels that can be placed with aw_place_level." )]
	public static void ListLevels()
	{
		var mount = AlanWakeMount.Current;
		if ( mount is null )
		{
			Log.Warning( "Alan Wake is not mounted. Enable the 'alan_wake' mount first." );
			return;
		}

		foreach ( var group in mount.CellsByLevel.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase ) )
		{
			int hd = group.Value.Count( c => c.HighDetailPath is not null );
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
		var mount = AlanWakeMount.Current;
		if ( mount is null ) { Log.Warning( "Alan Wake is not mounted." ); return; }

		var names = mount.CellsByLevel.Keys
			.Where( k => k.StartsWith( world + "/", StringComparison.OrdinalIgnoreCase ) )
			.Select( k => k[(world.Length + 1)..] )
			.ToArray();
		Place( world, names, $"Alan Wake - {world}" );
	}

	static void Place( string world, string[] levels, string rootName )
	{
		var mount = AlanWakeMount.Current;
		if ( mount is null ) { Log.Warning( "Alan Wake is not mounted." ); return; }

		var session = SceneEditorSession.Active;
		if ( session?.Scene is null ) { Log.Warning( "No active editor scene." ); return; }

		var cells = levels
			.SelectMany( l => mount.CellsByLevel.TryGetValue( $"{world}/{l}", out var list ) ? list : Enumerable.Empty<AwCellInfo>() )
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
				var levelCells = mount.CellsByLevel.TryGetValue( $"{world}/{level}", out var list ) ? list : null;
				if ( levelCells is null ) continue;

				var levelRoot = new GameObject { Name = level, Parent = root };

				foreach ( var cell in levelCells.OrderBy( c => c.X ).ThenBy( c => c.Z ) )
				{
					if ( Instantiate( cell.PrefabPath, levelRoot, $"cell_{cell.Name}" ) )
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
