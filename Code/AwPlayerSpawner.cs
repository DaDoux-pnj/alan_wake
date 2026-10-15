using System.Threading.Tasks;
using Sandbox.Network;


/// <summary>
/// Hosts the game and gives everyone who joins their own player, on the map's spawn point (Alan Wake maps have one
/// on open ground in the playable area). Like s&amp;box's Network Helper, but players wait for the map to finish
/// loading so nobody falls through the world before it exists; a few players at once are spread around the point.
/// On the host it also points the Map at the map picked in the New Game popup (see <see cref="AwMaps.Map"/>).
/// </summary>
[Title( "Player Spawner" )]
[Category( "Alan Wake" )]
[Icon( "person_pin_circle" )]
public sealed class AwPlayerSpawner : Component, Component.INetworkListener
{
	/// <summary>The player each person gets: a prefab with a PlayerController.</summary>
	[Property] public GameObject PlayerPrefab { get; set; }

	/// <summary>Create a lobby, unless we're joining someone else's.</summary>
	[Property] public bool StartServer { get; set; } = true;

	/// <summary>The map to wait for; the scene's first MapInstance when not set.</summary>
	[Property] public MapInstance Map { get; set; }

	readonly List<Connection> _waiting = new();
	bool _started;
	TimeSince _sinceStart;
	int _spawned;

	protected override async Task OnLoad()
	{
		if ( Scene.IsEditor )
			return;

		// The host plays the map picked in the New Game popup (the aw_map game setting); a map the game was launched
		// with still wins, through the Map's "Use Map From Launch". This has to run before the Map starts loading,
		// which is why this object sits above the Map in the scene. Joining players get the host's map instead.
		if ( !Networking.IsActive || Networking.IsHost )
		{
			Map ??= Scene.GetAllComponents<MapInstance>().FirstOrDefault();
			if ( Map.IsValid() )
				Map.MapName = AwMaps.PathOf( AwMaps.Map );
		}

		if ( !StartServer || Networking.IsActive )
			return;

		await Task.DelayRealtimeSeconds( 0.1f );
		Networking.CreateLobby( new LobbyConfig() );
	}

	protected override void OnStart()
	{
		Map ??= Scene.GetAllComponents<MapInstance>().FirstOrDefault();
		_started = true;
		_sinceStart = 0;
	}

	/// <summary>Someone has loaded in (called on the host). They get their player once the map is ready.</summary>
	public void OnActive( Connection connection )
	{
		_waiting.Add( connection );
	}

	public void OnDisconnected( Connection connection )
	{
		_waiting.Remove( connection );
	}

	protected override void OnUpdate()
	{
		if ( _waiting.Count == 0 || !MapReady || !PlayerPrefab.IsValid() )
			return;

		foreach ( var connection in _waiting )
		{
			var player = PlayerPrefab.Clone( SpawnLocation(), name: $"Player - {connection.Name}" );
			player.NetworkSpawn( connection );
		}

		_waiting.Clear();
	}

	/// <summary>
	/// True once the map has loaded, or when there's nothing to wait for: no map, or a load that has stopped (it
	/// failed) for a moment.
	/// </summary>
	bool MapReady => _started && (!Map.IsValid()
		|| string.IsNullOrWhiteSpace( Map.MapName )
		|| Map.IsLoaded
		|| ((Map.GameObject.Flags & GameObjectFlags.Loading) == 0 && _sinceStart > 2));

	/// <summary>
	/// A spawn point of the map (facing its way), or this object's position without one. Everyone after the first
	/// player lands a little to the side and a bit higher, so nobody spawns inside someone else.
	/// </summary>
	Transform SpawnLocation()
	{
		var spawns = Scene.GetAllComponents<SpawnPoint>().ToArray();
		var location = (spawns.Length > 0 ? Game.Random.FromArray( spawns ).WorldTransform : WorldTransform).WithScale( 1 );

		int index = _spawned++;
		if ( index > 0 )
			location = location.WithPosition( location.Position + Rotation.FromYaw( index * 137.5f ).Forward * 64 + Vector3.Up * 48 );

		return location;
	}
}
