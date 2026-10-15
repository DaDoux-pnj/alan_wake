using System;
using System.Buffers;

/// <summary>
/// Draws every placed copy of one Alan Wake foliage type (a tree species or plant) in a world cell with GPU
/// instancing: copies closer than <see cref="MeshDistance"/> use the full mesh, copies up to
/// <see cref="MaxDistance"/> use the game's billboard impostor, anything further is skipped.
/// Added by the Alan Wake mount to its cell prefabs.
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

	/// <summary>Copies of the model, relative to this GameObject.</summary>
	[Property] public List<Transform> Instances { get; set; } = new();

	FoliageSceneObject _sceneObject;
	Transform _builtFor;

	protected override void OnEnabled() => Rebuild();
	protected override void OnDisabled() => Release();
	protected override void OnDestroy() => Release();
	protected override void OnValidate() => Rebuild();

	protected override void OnPreRender()
	{
		if ( _sceneObject is not null && !WorldTransform.Equals( _builtFor ) )
			Rebuild();
	}

	void Release()
	{
		_sceneObject?.Delete();
		_sceneObject = null;
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

		_sceneObject = new FoliageSceneObject( Scene.SceneWorld, Model, Impostor, transforms, MeshDistance, MaxDistance );
		_sceneObject.Bounds = bounds;
		_sceneObject.Flags.IsOpaque = true;
		_sceneObject.Flags.CastShadows = CastShadows;
		_builtFor = world;
	}
}

/// <summary>Picks mesh/impostor/nothing per copy from the rendering camera, then draws both groups instanced.</summary>
sealed class FoliageSceneObject : SceneCustomObject
{
	readonly Model _mesh;
	readonly Model _impostor;
	readonly Transform[] _transforms;
	readonly float _meshDistanceSq;
	readonly float _maxDistanceSq;

	public FoliageSceneObject( SceneWorld world, Model mesh, Model impostor, Transform[] transforms, float meshDistance, float maxDistance ) : base( world )
	{
		_mesh = mesh;
		_impostor = impostor;
		_transforms = transforms;
		_meshDistanceSq = meshDistance * meshDistance;
		_maxDistanceSq = maxDistance * maxDistance;
	}

	public override void RenderSceneObject()
	{
		var camera = Graphics.CameraPosition;
		int count = _transforms.Length;
		var near = ArrayPool<Transform>.Shared.Rent( count );
		var far = ArrayPool<Transform>.Shared.Rent( count );

		try
		{
			int nearCount = 0, farCount = 0;
			for ( int i = 0; i < count; i++ )
			{
				float distanceSq = (_transforms[i].Position - camera).LengthSquared;
				if ( distanceSq < _meshDistanceSq )
					near[nearCount++] = _transforms[i];
				else if ( distanceSq < _maxDistanceSq && _impostor is not null )
					far[farCount++] = _transforms[i];
			}

			if ( nearCount > 0 )
				Graphics.DrawModelInstanced( _mesh, near.AsSpan( 0, nearCount ) );
			if ( farCount > 0 )
				Graphics.DrawModelInstanced( _impostor, far.AsSpan( 0, farCount ) );
		}
		finally
		{
			ArrayPool<Transform>.Shared.Return( near );
			ArrayPool<Transform>.Shared.Return( far );
		}
	}
}
