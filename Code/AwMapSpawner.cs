/// <summary>
/// Holds the player until the scene's map has loaded, then puts them on the map's spawn point (Alan Wake maps have
/// one on open ground in the playable area), facing the way it faces. Without a map, or with a map that has no spawn
/// point, the player stays where it was placed.
/// </summary>
[Title( "Map Spawner" )]
[Category( "Alan Wake" )]
[Icon( "person_pin_circle" )]
public sealed class AwMapSpawner : Component
{
	/// <summary>The player to place: the GameObject with the PlayerController.</summary>
	[Property] public GameObject Player { get; set; }

	/// <summary>The map to wait for; the scene's first MapInstance when not set.</summary>
	[Property] public MapInstance Map { get; set; }

	bool _placed;
	TimeSince _sinceStart;

	protected override void OnStart()
	{
		Map ??= Scene.GetAllComponents<MapInstance>().FirstOrDefault();
		_sinceStart = 0;

		if ( Player.IsValid() && !MapReady )
			Player.Enabled = false;
	}

	protected override void OnUpdate()
	{
		if ( _placed || !MapReady )
			return;

		_placed = true;
		if ( !Player.IsValid() )
			return;

		var spawn = Scene.GetAllComponents<SpawnPoint>().FirstOrDefault();
		if ( spawn.IsValid() )
		{
			float yaw = spawn.WorldRotation.Yaw();
			Player.WorldPosition = spawn.WorldPosition;
			Player.WorldRotation = Rotation.FromYaw( yaw );

			var controller = Player.GetComponent<PlayerController>( true );
			if ( controller.IsValid() )
				controller.EyeAngles = new Angles( 0, yaw, 0 );
		}

		Player.Enabled = true;
	}

	/// <summary>
	/// True once the map has loaded, or when there's nothing to wait for: no map, or a load that has stopped (it
	/// failed) for a moment.
	/// </summary>
	bool MapReady => !Map.IsValid()
		|| string.IsNullOrWhiteSpace( Map.MapName )
		|| Map.IsLoaded
		|| ((Map.GameObject.Flags & GameObjectFlags.Loading) == 0 && _sinceStart > 2);
}
