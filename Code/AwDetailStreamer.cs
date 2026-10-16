/// <summary>
/// Streams the detail of an Alan Wake map the way the game streams its high detail cells around the player: props,
/// physics props, lights, undergrowth, grass and ambient sounds run within <see cref="Distance"/> of the camera and
/// switch off beyond it, where the world keeps its terrain, landmarks and trees. The mount adds this to its maps and
/// puts each cell's detail in one group, tagged "aw_detail" and centred on the cell.
/// </summary>
[Title( "Detail Streamer" )]
[Category( "Alan Wake" )]
[Icon( "radar" )]
public sealed class AwDetailStreamer : Component
{
	/// <summary>Inches from the camera to a cell's centre within which its detail runs.</summary>
	[Property] public float Distance { get; set; } = 11811;

	/// <summary>Milliseconds a frame may spend switching cells on and off, so moving around doesn't stutter.</summary>
	[Property] public float FrameBudget { get; set; } = 2f;

	/// <summary>Cells switch off this much further out than they switch on, so they don't flicker at the edge.</summary>
	const float Hysteresis = 1.15f;

	readonly List<(GameObject Group, Vector2 Position)> _groups = new();
	readonly List<(float DistanceSquared, int Index)> _switchOn = new();
	readonly List<int> _switchOff = new();

	protected override void OnStart()
	{
		_groups.Clear();
		foreach ( var go in GameObject.GetAllObjects( false ) )
		{
			// The group's own tag only: children inherit it, and the group's "content" child sits on the map's origin.
			if ( go.Tags.Has( "aw_detail", false ) )
				_groups.Add( (go, new Vector2( go.WorldPosition.x, go.WorldPosition.y )) );
		}

		// Players appear on the map's spawn point: settle that spot in one go, while the map is still loading, rather
		// than a few cells a frame in front of them.
		var focus = Scene.GetAllComponents<SpawnPoint>().FirstOrDefault()?.WorldPosition ?? Scene.Camera?.WorldPosition;
		if ( focus.HasValue )
			Stream( focus.Value, float.PositiveInfinity );
	}

	protected override void OnUpdate()
	{
		if ( Scene.Camera is { } camera )
			Stream( camera.WorldPosition, FrameBudget );
	}

	/// <summary>Switches the groups that crossed the distance, nearest first, until the time budget runs out.</summary>
	void Stream( Vector3 focus, float budgetMilliseconds )
	{
		float onSquared = Distance * Distance;
		float offSquared = onSquared * Hysteresis * Hysteresis;
		var at = new Vector2( focus.x, focus.y );

		_switchOn.Clear();
		_switchOff.Clear();
		for ( int i = 0; i < _groups.Count; i++ )
		{
			var (group, position) = _groups[i];
			if ( !group.IsValid() )
				continue;

			float distanceSquared = (position - at).LengthSquared;
			if ( !group.Enabled && distanceSquared < onSquared )
				_switchOn.Add( (distanceSquared, i) );
			else if ( group.Enabled && distanceSquared > offSquared )
				_switchOff.Add( i );
		}

		if ( _switchOn.Count == 0 && _switchOff.Count == 0 )
			return;

		_switchOn.Sort( ( a, b ) => a.DistanceSquared.CompareTo( b.DistanceSquared ) );

		double start = RealTime.NowDouble;
		foreach ( var (_, i) in _switchOn )
		{
			_groups[i].Group.Enabled = true;
			if ( (RealTime.NowDouble - start) * 1000 > budgetMilliseconds )
				return;
		}

		foreach ( var i in _switchOff )
		{
			_groups[i].Group.Enabled = false;
			if ( (RealTime.NowDouble - start) * 1000 > budgetMilliseconds )
				return;
		}
	}
}
