using System;
using System.Buffers;

/// <summary>
/// Every placed copy of one Alan Wake foliage type (a tree species or plant) in a world cell: copies closer than
/// <see cref="MeshDistance"/> draw the full mesh, copies up to <see cref="MaxDistance"/> the game's billboard
/// impostor, anything further nothing. The meshes sway in the game's wind (<see cref="AwFoliageWind"/>). Added by the
/// Alan Wake mount to its cell prefabs; all renderers of one foliage type draw together (<see cref="FoliageBatch"/>).
/// </summary>
[Title( "Alan Wake Foliage" )]
[Category( "Alan Wake" )]
[Icon( "forest" )]
public sealed class AwFoliageRenderer : Component, Component.ExecuteInEditor
{
	[Property] public Model Model { get; set; }
	[Property] public Model Impostor { get; set; }

	/// <summary>Inches. Copies nearer than this draw the full mesh.</summary>
	[Property] public float MeshDistance { get; set; } = 4000;

	/// <summary>Inches. Copies beyond this are not drawn at all.</summary>
	[Property] public float MaxDistance { get; set; } = 80000;

	[Property] public bool CastShadows { get; set; } = true;

	/// <summary>The game's weather wind the trees sway in: 0 (still) to 1 (storm). See <see cref="AwFoliageWind"/>.</summary>
	[Property, Range( 0, 1 )] public float Wind { get; set; } = 0.5f;

	/// <summary>Copies of the model, relative to this GameObject.</summary>
	[Property] public List<Transform> Instances { get; set; } = new();

	FoliageBatch _batch;
	FoliageBatch.Tile _tile;
	Transform _builtFor;

	// No per frame callback: a map holds thousands of these, and s&box calls every component that has one each frame
	// even when there's nothing to do. Moves come in as transform changes, the wind from AwFoliageWindSystem.
	protected override void OnEnabled()
	{
		Transform.OnTransformChanged += OnMoved;
		Rebuild();
	}

	protected override void OnDisabled()
	{
		Transform.OnTransformChanged -= OnMoved;
		Release();
	}

	protected override void OnDestroy() => Release();
	protected override void OnValidate() => Rebuild();

	void OnMoved()
	{
		if ( _tile is not null && !WorldTransform.Equals( _builtFor ) )
			Rebuild();
	}

	void Release()
	{
		_batch?.Remove( _tile );
		_batch = null;
		_tile = null;
	}

	void Rebuild()
	{
		Release();

		if ( !Active || Scene?.SceneWorld is null || Model is null || Instances is null || Instances.Count == 0 )
			return;

		var world = WorldTransform;
		var transforms = new Transform[Instances.Count];
		var modelBounds = Model.Bounds;
		BBox bounds = default;
		for ( int i = 0; i < transforms.Length; i++ )
		{
			transforms[i] = world.ToWorld( Instances[i] );
			var b = modelBounds.Transform( transforms[i] );
			bounds = i == 0 ? b : bounds.AddBBox( b );
		}

		_batch = FoliageBatch.For( Scene.SceneWorld, Model, Impostor, MeshDistance, MaxDistance, CastShadows );
		_tile = _batch.Add( transforms, bounds.Grow( 100f ) ); // room for the branches swaying out
		_builtFor = world;
		AwFoliageWind.Strength = Wind;
	}
}

/// <summary>
/// Every copy of one foliage type in a scene world, drawn together. Each renderer adds its copies as a tile (one per
/// world cell). Per view, tiles out of the view or out of range are skipped whole, tiles entirely near or entirely
/// far go in whole, and only tiles on the boundary are sorted copy by copy; everything then goes out as one instanced
/// mesh draw and one impostor draw. That is two draws per foliage type and view, where one object per cell made two
/// per cell.
/// </summary>
sealed class FoliageBatch : SceneCustomObject
{
	/// <summary>One renderer's copies, in world space, and their bounds.</summary>
	public sealed class Tile
	{
		public Transform[] Transforms;
		public BBox Bounds;
		public int Index;
	}

	static readonly Dictionary<(SceneWorld World, Model Mesh, Model Impostor, float MeshDistance, float MaxDistance, bool CastShadows), FoliageBatch> Batches = new();

	readonly (SceneWorld World, Model Mesh, Model Impostor, float MeshDistance, float MaxDistance, bool CastShadows) _key;
	readonly List<Tile> _tiles = new();
	readonly float _meshDistanceSq;
	readonly float _maxDistanceSq;
	int _instanceCount;

	FoliageBatch( (SceneWorld World, Model Mesh, Model Impostor, float MeshDistance, float MaxDistance, bool CastShadows) key ) : base( key.World )
	{
		_key = key;
		_meshDistanceSq = key.MeshDistance * key.MeshDistance;
		_maxDistanceSq = key.MaxDistance * key.MaxDistance;
		Flags.IsOpaque = true;
		Flags.CastShadows = key.CastShadows;
	}

	/// <summary>The batch drawing a foliage type in a scene world, made on first use.</summary>
	public static FoliageBatch For( SceneWorld world, Model mesh, Model impostor, float meshDistance, float maxDistance, bool castShadows )
	{
		var key = (world, mesh, impostor, meshDistance, maxDistance, castShadows);
		if ( !Batches.TryGetValue( key, out var batch ) || !batch.IsValid() )
			Batches[key] = batch = new FoliageBatch( key );
		return batch;
	}

	public Tile Add( Transform[] transforms, BBox bounds )
	{
		var tile = new Tile { Transforms = transforms, Bounds = bounds, Index = _tiles.Count };
		_tiles.Add( tile );
		_instanceCount += transforms.Length;

		// Like the per cell objects before, the engine culls the batch by bounds (and picks its shadow cascades); this
		// only grows, removing tiles leaves it as it is.
		Bounds = _tiles.Count == 1 ? bounds : Bounds.AddBBox( bounds );
		return tile;
	}

	/// <summary>Takes a renderer's copies out again; the batch goes away with its last tile.</summary>
	public void Remove( Tile tile )
	{
		if ( tile is null || tile.Index >= _tiles.Count || _tiles[tile.Index] != tile )
			return;

		var last = _tiles[_tiles.Count - 1];
		_tiles[tile.Index] = last;
		last.Index = tile.Index;
		_tiles.RemoveAt( _tiles.Count - 1 );
		_instanceCount -= tile.Transforms.Length;

		if ( _tiles.Count > 0 )
			return;

		if ( Batches.TryGetValue( _key, out var current ) && current == this )
			Batches.Remove( _key );
		Delete();
	}

	public override void RenderSceneObject()
	{
		if ( _tiles.Count == 0 )
			return;

		var camera = Graphics.CameraPosition;
		var frustum = Graphics.Frustum;
		bool impostors = _key.Impostor is not null;
		var near = ArrayPool<Transform>.Shared.Rent( _instanceCount );
		var far = ArrayPool<Transform>.Shared.Rent( _instanceCount );

		try
		{
			int nearCount = 0, farCount = 0;
			foreach ( var tile in _tiles )
			{
				float closest = ClosestSquared( camera, tile.Bounds );
				if ( closest >= _maxDistanceSq || (closest >= _meshDistanceSq && !impostors) || !frustum.IsInside( tile.Bounds, true ) )
					continue;

				var transforms = tile.Transforms;
				float furthest = FurthestSquared( camera, tile.Bounds );
				if ( furthest < _meshDistanceSq )
				{
					transforms.AsSpan().CopyTo( near.AsSpan( nearCount ) );
					nearCount += transforms.Length;
				}
				else if ( closest >= _meshDistanceSq && furthest < _maxDistanceSq )
				{
					transforms.AsSpan().CopyTo( far.AsSpan( farCount ) );
					farCount += transforms.Length;
				}
				else
				{
					for ( int i = 0; i < transforms.Length; i++ )
					{
						float distanceSq = (transforms[i].Position - camera).LengthSquared;
						if ( distanceSq < _meshDistanceSq )
							near[nearCount++] = transforms[i];
						else if ( distanceSq < _maxDistanceSq && impostors )
							far[farCount++] = transforms[i];
					}
				}
			}

			if ( nearCount > 0 )
			{
				var wind = AwFoliageWind.Buffer;
				if ( wind is not null )
				{
					Graphics.Attributes.Set( AwFoliageWind.TablesAttribute, wind );
					Graphics.Attributes.Set( AwFoliageWind.EnabledAttribute, 1f );
				}

				Graphics.DrawModelInstanced( _key.Mesh, near.AsSpan( 0, nearCount ) );
			}

			if ( farCount > 0 )
				Graphics.DrawModelInstanced( _key.Impostor, far.AsSpan( 0, farCount ) );
		}
		finally
		{
			ArrayPool<Transform>.Shared.Return( near );
			ArrayPool<Transform>.Shared.Return( far );
		}
	}

	/// <summary>Squared distance from a point to the nearest point of a box (0 inside it).</summary>
	static float ClosestSquared( Vector3 point, BBox box )
	{
		float x = MathF.Max( MathF.Max( box.Mins.x - point.x, point.x - box.Maxs.x ), 0f );
		float y = MathF.Max( MathF.Max( box.Mins.y - point.y, point.y - box.Maxs.y ), 0f );
		float z = MathF.Max( MathF.Max( box.Mins.z - point.z, point.z - box.Maxs.z ), 0f );
		return x * x + y * y + z * z;
	}

	/// <summary>Squared distance from a point to the furthest corner of a box.</summary>
	static float FurthestSquared( Vector3 point, BBox box )
	{
		float x = MathF.Max( MathF.Abs( point.x - box.Mins.x ), MathF.Abs( point.x - box.Maxs.x ) );
		float y = MathF.Max( MathF.Abs( point.y - box.Mins.y ), MathF.Abs( point.y - box.Maxs.y ) );
		float z = MathF.Max( MathF.Abs( point.z - box.Mins.z ), MathF.Abs( point.z - box.Maxs.z ) );
		return x * x + y * y + z * z;
	}
}
