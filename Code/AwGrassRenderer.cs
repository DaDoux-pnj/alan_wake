using System;
using System.Buffers;

/// <summary>
/// Grows and draws an Alan Wake world cell's grass around the camera, the way the game does
/// (TessellatorThread::growGrass): for every terrain poly near the camera, each of its 16 x 16 grid cells spawns
/// density / 16 clumps per square metre of the two clump types its terrain layer asks for, at random spots and yaw,
/// standing on the terrain surface along its normal. Added by the Alan Wake mount to its cell prefabs.
/// </summary>
[Title( "Alan Wake Grass" )]
[Category( "Alan Wake" )]
[Icon( "grass" )]
public sealed class AwGrassRenderer : Component, Component.ExecuteInEditor
{
	/// <summary>The game's grass clumps, indexed by the clump numbers in <see cref="Data"/>.</summary>
	[Property] public List<Model> Clumps { get; set; } = new();

	/// <summary>Base64 per terrain poly records written by the mount (corners, heights, layer codes, densities, brightness).</summary>
	[Property, Hide] public string Data { get; set; }

	/// <summary>Terrain grid segments per poly edge, as the cell's terrain model was built with.</summary>
	[Property] public int Subdivisions { get; set; } = 5;

	/// <summary>Inches. Grass grows up to this distance from the camera.</summary>
	[Property] public float MaxDistance { get; set; } = 1000;

	/// <summary>Multiplier on the game's grass densities.</summary>
	[Property] public float Density { get; set; } = 1;

	/// <summary>Grid cells the game shades darker than this (under porches, in sheds) get no grass.</summary>
	[Property] public float MinBrightness { get; set; } = 0.3f;

	[Property] public bool CastShadows { get; set; }

	GrassSceneObject _sceneObject;
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

		if ( !Active || Scene?.SceneWorld is null || Clumps is null || Clumps.Count == 0 || string.IsNullOrEmpty( Data ) )
			return;

		GrassPoly[] polys;
		try
		{
			polys = GrassPoly.Decode( Convert.FromBase64String( Data ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Alan Wake grass data: {ex.Message}" );
			return;
		}

		if ( polys.Length == 0 )
			return;

		var world = WorldTransform;
		var clumps = new Model[Clumps.Count];
		for ( int i = 0; i < clumps.Length; i++ )
			clumps[i] = Clumps[i] is { IsError: false } model ? model : null;

		_sceneObject = new GrassSceneObject( Scene.SceneWorld, clumps, polys, world, Subdivisions, MaxDistance, Density, MinBrightness );
		_sceneObject.Flags.IsOpaque = true;
		_sceneObject.Flags.CastShadows = CastShadows;
		_builtFor = world;
	}
}

/// <summary>One terrain poly's grass source: its surface and the game's grass layout over it.</summary>
sealed class GrassPoly
{
	const int RecordSize = 2 + 48 + 48 + 512 + 64 + 6 + 6 + 256;

	public int Seed;
	public readonly Vector3[] Corners = new Vector3[4];
	public readonly Vector3[] Displacement = new Vector3[4];
	public readonly short[] Heights = new short[256];
	public readonly byte[] Codes = new byte[64];
	public readonly byte[] Clumps = new byte[6];
	public readonly byte[] Densities = new byte[6];
	public readonly byte[] Brightness = new byte[256];
	public BBox Bounds;

	public static GrassPoly[] Decode( byte[] data )
	{
		int count = data.Length / RecordSize;
		var polys = new GrassPoly[count];
		for ( int i = 0; i < count; i++ )
		{
			int o = i * RecordSize;
			var p = new GrassPoly { Seed = BitConverter.ToUInt16( data, o ) };
			o += 2;
			for ( int k = 0; k < 4; k++, o += 12 )
				p.Corners[k] = new Vector3( BitConverter.ToSingle( data, o ), BitConverter.ToSingle( data, o + 4 ), BitConverter.ToSingle( data, o + 8 ) );
			for ( int k = 0; k < 4; k++, o += 12 )
				p.Displacement[k] = new Vector3( BitConverter.ToSingle( data, o ), BitConverter.ToSingle( data, o + 4 ), BitConverter.ToSingle( data, o + 8 ) );
			for ( int k = 0; k < 256; k++, o += 2 )
				p.Heights[k] = BitConverter.ToInt16( data, o );
			Array.Copy( data, o, p.Codes, 0, 64 ); o += 64;
			Array.Copy( data, o, p.Clumps, 0, 6 ); o += 6;
			Array.Copy( data, o, p.Densities, 0, 6 ); o += 6;
			Array.Copy( data, o, p.Brightness, 0, 256 );

			var bounds = new BBox( p.GridPoint( 0, 0 ), p.GridPoint( 0, 0 ) );
			for ( int r = 0; r < 16; r += 3 )
			for ( int c = 0; c < 16; c += 3 )
				bounds = bounds.AddPoint( p.GridPoint( r, c ) );
			p.Bounds = bounds.AddPoint( p.GridPoint( 15, 15 ) );
			polys[i] = p;
		}
		return polys;
	}

	public static Vector3 Bilinear( Vector3[] p, float s, float t )
	{
		var a = p[0] + (p[1] - p[0]) * s;
		var b = p[3] + (p[2] - p[3]) * s;
		return a + (b - a) * t;
	}

	/// <summary>A point of the poly's full 16 x 16 displaced grid, placed as the terrain mesh places its vertices.</summary>
	public Vector3 GridPoint( int row, int column )
	{
		float s = column / 15f, t = row / 15f;
		return Bilinear( Corners, s, t ) + Bilinear( Displacement, s, t ) * Heights[row * 16 + column];
	}

	/// <summary>The terrain mesh's surface (built with <paramref name="segments"/> per edge) at a poly coordinate.</summary>
	public void Surface( float s, float t, int segments, out Vector3 position, out Vector3 normal )
	{
		float x = s * segments, y = t * segments;
		int j = Math.Clamp( (int)x, 0, segments - 1 ), i = Math.Clamp( (int)y, 0, segments - 1 );
		float u = Math.Clamp( x - j, 0, 1 ), v = Math.Clamp( y - i, 0, 1 );

		var q00 = GridPoint( i * 15 / segments, j * 15 / segments );
		var q01 = GridPoint( i * 15 / segments, (j + 1) * 15 / segments );
		var q10 = GridPoint( (i + 1) * 15 / segments, j * 15 / segments );
		var q11 = GridPoint( (i + 1) * 15 / segments, (j + 1) * 15 / segments );

		// The mesh splits each grid quad along q00-q11.
		if ( u >= v )
		{
			position = q00 + (q01 - q00) * u + (q11 - q01) * v;
			normal = Vector3.Cross( q01 - q00, q11 - q01 );
		}
		else
		{
			position = q00 + (q10 - q00) * v + (q11 - q10) * u;
			normal = Vector3.Cross( q11 - q10, q10 - q00 );
		}

		normal = normal.LengthSquared > 1e-6f ? normal.Normal : Vector3.Up;
		if ( normal.z < 0 ) normal = -normal;
	}
}

/// <summary>Grows grass for the polys near the rendering camera (a few per frame), forgets far ones, draws instanced.</summary>
sealed class GrassSceneObject : SceneCustomObject
{
	const float InchesPerMetre = 39.3701f;
	const int PolysGrownPerFrame = 6;

	sealed class Grown
	{
		public List<Transform>[] Transforms;
		public List<float>[] CutoffsSq;
	}

	readonly Model[] _clumps;
	readonly GrassPoly[] _polys;
	readonly BBox[] _worldBounds;
	readonly Transform _world;
	readonly int _segments;
	readonly float _maxDistance;
	readonly float _density;
	readonly byte _minBrightness;
	readonly Dictionary<int, Grown> _grown = new();
	readonly object _lock = new();

	public GrassSceneObject( SceneWorld world, Model[] clumps, GrassPoly[] polys, Transform transform, int segments, float maxDistance, float density, float minBrightness ) : base( world )
	{
		_clumps = clumps;
		_polys = polys;
		_world = transform;
		_segments = segments is 1 or 3 or 5 or 15 ? segments : 5;
		_maxDistance = MathF.Max( 1, maxDistance );
		_density = MathF.Max( 0, density );
		_minBrightness = (byte)Math.Clamp( minBrightness * 255f, 0, 255 );

		_worldBounds = new BBox[polys.Length];
		BBox all = default;
		for ( int i = 0; i < polys.Length; i++ )
		{
			// Clumps stand up to about a metre above the ground.
			var b = polys[i].Bounds.Transform( transform );
			_worldBounds[i] = new BBox( b.Mins, b.Maxs + Vector3.Up * InchesPerMetre );
			all = i == 0 ? _worldBounds[i] : all.AddBBox( _worldBounds[i] );
		}
		Bounds = all;
	}

	static float DistanceSq( BBox box, Vector3 point )
	{
		var closest = Vector3.Max( box.Mins, Vector3.Min( point, box.Maxs ) );
		return (closest - point).LengthSquared;
	}

	Grown Grow( GrassPoly poly )
	{
		var grown = new Grown { Transforms = new List<Transform>[_clumps.Length], CutoffsSq = new List<float>[_clumps.Length] };
		var random = new Random( poly.Seed * 7919 + 101 );
		float metresSq = InchesPerMetre * InchesPerMetre;

		for ( int r = 0; r < 16; r++ )
		for ( int c = 0; c < 16; c++ )
		{
			int k = r * 16 + c;
			int code = (poly.Codes[k >> 2] >> ((k & 3) * 2)) & 3;
			if ( code == 3 || poly.Brightness[k] < _minBrightness )
				continue;

			var p00 = GrassPoly.Bilinear( poly.Corners, c / 16f, r / 16f );
			var p10 = GrassPoly.Bilinear( poly.Corners, (c + 1) / 16f, r / 16f );
			var p01 = GrassPoly.Bilinear( poly.Corners, c / 16f, (r + 1) / 16f );
			float area = (p10 - p00).Length * (p01 - p00).Length / metresSq;

			for ( int slot = 0; slot < 2; slot++ )
			{
				int clump = poly.Clumps[code * 2 + slot];
				float density = poly.Densities[code * 2 + slot] / 16f * _density;
				if ( density <= 0 || clump >= _clumps.Length || _clumps[clump] is null )
					continue;

				float expected = density * area;
				int count = (int)expected;
				if ( random.NextSingle() < expected - count )
					count++;

				for ( int n = 0; n < count; n++ )
				{
					float s = (c + random.NextSingle()) / 16f;
					float t = (r + random.NextSingle()) / 16f;
					float yaw = random.NextSingle() * 360f;
					float cutoff = _maxDistance * (0.8f + 0.2f * random.NextSingle());

					poly.Surface( s, t, _segments, out var position, out var normal );
					var rotation = Rotation.FromToRotation( Vector3.Up, normal ) * Rotation.FromAxis( Vector3.Up, yaw );

					(grown.Transforms[clump] ??= new List<Transform>()).Add( _world.ToWorld( new Transform( position, rotation ) ) );
					(grown.CutoffsSq[clump] ??= new List<float>()).Add( cutoff * cutoff );
				}
			}
		}

		return grown;
	}

	public override void RenderSceneObject()
	{
		var camera = Graphics.CameraPosition;
		float maxSq = _maxDistance * _maxDistance;
		float forgetSq = maxSq * 2.25f;

		lock ( _lock )
		{
			int budget = PolysGrownPerFrame;
			for ( int i = 0; i < _polys.Length; i++ )
			{
				float d = DistanceSq( _worldBounds[i], camera );
				if ( d > forgetSq )
				{
					_grown.Remove( i );
					continue;
				}

				if ( d <= maxSq && budget > 0 && !_grown.ContainsKey( i ) )
				{
					_grown[i] = Grow( _polys[i] );
					budget--;
				}
			}

			if ( _grown.Count == 0 )
				return;

			for ( int k = 0; k < _clumps.Length; k++ )
			{
				if ( _clumps[k] is null )
					continue;

				int total = 0;
				foreach ( var grown in _grown.Values )
					total += grown.Transforms[k]?.Count ?? 0;
				if ( total == 0 )
					continue;

				var buffer = ArrayPool<Transform>.Shared.Rent( total );
				try
				{
					int count = 0;
					foreach ( var grown in _grown.Values )
					{
						var transforms = grown.Transforms[k];
						if ( transforms is null )
							continue;

						var cutoffs = grown.CutoffsSq[k];
						for ( int n = 0; n < transforms.Count; n++ )
						{
							if ( (transforms[n].Position - camera).LengthSquared < cutoffs[n] )
								buffer[count++] = transforms[n];
						}
					}

					if ( count > 0 )
						Graphics.DrawModelInstanced( _clumps[k], buffer.AsSpan( 0, count ) );
				}
				finally
				{
					ArrayPool<Transform>.Shared.Return( buffer );
				}
			}
		}
	}
}
