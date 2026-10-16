using System;

/// <summary>Moves the foliage wind on once a frame, in scenes that have Alan Wake foliage.</summary>
public sealed class AwFoliageWindSystem : GameObjectSystem
{
	public AwFoliageWindSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, Tick, "AwFoliageWind" );
	}

	void Tick()
	{
		if ( AwFoliageWind.Strength is float strength )
			AwFoliageWind.Update( strength );
	}
}

/// <summary>
/// Alan Wake's foliage wind, ported from its renderer (the FoliageEngine and wind tables in renderer_sf_Win32.dll):
/// rows of damped springs pushed around by noise and gusts. The vertices of a tree say which spring moves them and how
/// much (the mount stores that in their colour), so trunks lean, branches sway and leaves flutter, each out of step
/// with the others. Simulated once per frame and handed to shaders/aw_foliage.shader as one buffer.
/// </summary>
public static class AwFoliageWind
{
	/// <summary>Shader buffer: [0, 32) small plants, [32, 64) large trees, [64, 80) trunks, [80, 96) leaves.</summary>
	public const string TablesAttribute = "AwWindTables";

	/// <summary>Shader switch, 1 while the tables are bound.</summary>
	public const string EnabledAttribute = "AwWind";

	const int LeafCount = 16;
	const int TableCount = 96;

	// The game's tuning ("Foliage:Small Wind Strength" etc.): strength, stiffness, frequency, amplitude.
	static readonly WindSprings Small = new( 32, 10f, 0.9f, 1f, 1f );
	static readonly WindSprings Large = new( 32, 30f, 0.85f, 0.5f, 3f );
	static readonly WindSprings Trunk = new( 16, 15f, 0.98f, 0.2f, 0.4f );
	const float LeafFrequency = 2.5f;
	const float LeafAmplitude = 0.6f;

	static readonly Vector4[] Data = new Vector4[TableCount];
	static float _leafPhase;
	static double _lastTime;
	static GpuBuffer<Vector4> _buffer;

	/// <summary>This frame's tables, or null before the first <see cref="Update"/>.</summary>
	public static GpuBuffer<Vector4> Buffer => _buffer;

	/// <summary>The weather wind of the foliage placed so far (set by AwFoliageRenderer), or null while there's none.</summary>
	public static float? Strength { get; set; }

	/// <summary>
	/// Moves the wind on to now and uploads it (<see cref="AwFoliageWindSystem"/> calls this every frame of every scene;
	/// only the first call of a frame does anything). <paramref name="strength"/> is the game's weather wind, 0 (still)
	/// to 1 (storm).
	/// </summary>
	public static void Update( float strength )
	{
		double now = RealTime.NowDouble;
		double elapsed = now - _lastTime;
		if ( elapsed >= 0 && elapsed < 0.004 )
			return;

		_lastTime = now;
		float dt = Math.Clamp( (float)elapsed, 0.005f, 0.1f );

		// Stronger weather makes the springs faster and wider (FoliageEngine's update).
		float w = Math.Clamp( strength, 0f, 1f );
		float frequency = 0.5f + w;
		float amplitude = (0.5f + 0.5f * w) * Math.Clamp( 3f * w, 0f, 1f );

		Small.Step( dt, frequency, amplitude );
		Large.Step( dt, frequency, amplitude );
		Trunk.Step( dt, frequency, amplitude );
		Small.CopyTo( Data, 0 );
		Large.CopyTo( Data, 32 );
		Trunk.CopyTo( Data, 64 );

		// Leaves: plain noise, as hard as their branch is moving (the shader multiplies by the branch spring's w).
		_leafPhase += LeafFrequency * dt;
		for ( int i = 0; i < LeafCount; i++ )
		{
			float a = _leafPhase * 0.9894f + i * 1.5345f + 56.254f;
			float b = _leafPhase * 1.0543f + i * 1.8255f + 6.957f;
			float c = _leafPhase * 0.9924f + i * 1.2353f + 13.369f;
			var leaf = new Vector3( TwoOctaves( a ), TwoOctaves( b ), TwoOctaves( c ) ) * LeafAmplitude;
			Data[80 + i] = ToObjectSpace( leaf, 0f );
		}

		if ( _buffer is null )
			_buffer = new GpuBuffer<Vector4>( TableCount, GpuBuffer.UsageFlags.Structured, "aw foliage wind" );

		_buffer.SetData( Data.AsSpan() );
	}

	/// <summary>The game's axes (y up, metres) to a model's (z up); lengths stay in metres.</summary>
	static Vector4 ToObjectSpace( Vector3 game, float w ) => new( game.x, game.z, game.y, w );

	/// <summary>One of the game's wind tables (WindTable::update): a spring per entry, driven by noise.</summary>
	sealed class WindSprings
	{
		const float Damping = 0.01f;

		readonly Vector3[] _position;
		readonly Vector3[] _previous;
		readonly Vector4[] _output;
		readonly float _strength;
		readonly float _stiffness;
		readonly float _frequency;
		readonly float _amplitude;
		float _time;
		float _lastStep = -1f;

		public WindSprings( int count, float strength, float stiffness, float frequency, float amplitude )
		{
			_position = new Vector3[count];
			_previous = new Vector3[count];
			_output = new Vector4[count];
			_strength = strength;
			_stiffness = stiffness;
			_frequency = frequency;
			_amplitude = amplitude;
		}

		public void Step( float dt, float frequencyScale, float amplitudeScale )
		{
			float step = _frequency * frequencyScale * dt;
			_time += step;

			// Gusts: slow noise between calm (0) and full strength (1), shared by the whole table.
			float g = _time * 0.15f;
			float gust = Math.Clamp( 0.8f * (Noise( 2f * g ) * 0.5f + Noise( g ) + SignedNoise( 4f * g ) * 0.25f), 0f, 1f );

			float decay = MathF.Pow( 1f - _stiffness, step );
			float ratio = _lastStep > 0f ? step / _lastStep : 1f;
			_lastStep = step;

			float push = _strength * gust * step / 30f;
			float amplitude = _amplitude * amplitudeScale;
			for ( int i = 0; i < _position.Length; i++ )
			{
				float a = _time * 1.342348f + 12.5435f + i * 3.036374f;
				float c = _time * 1.224245f + 53.3633f + i * 2.25f;
				var force = new Vector3( ThreeOctaves( a ), 0f, ThreeOctaves( c ) ) * push;

				var position = _position[i] * decay;
				var velocity = position - _previous[i];
				_previous[i] = position;
				_position[i] = position + velocity * (ratio * (1f - Damping)) + force;

				var sway = _position[i] * amplitude;
				_output[i] = ToObjectSpace( sway, Math.Clamp( MathF.Pow( sway.Length, 1.5f ), 0f, 1f ) );
			}
		}

		public void CopyTo( Vector4[] data, int offset ) => Array.Copy( _output, 0, data, offset, _output.Length );
	}

	static float TwoOctaves( float x ) => SignedNoise( 2f * x ) * 0.5f + SignedNoise( x );
	static float ThreeOctaves( float x ) => SignedNoise( 2f * x ) * 0.5f + SignedNoise( x ) + SignedNoise( 4f * x ) * 0.25f;

	/// <summary>The game's m::noise: Perlin's improved noise along a line, 0..1.</summary>
	static float Noise( float x ) => Perlin( x ) + 0.5f;

	/// <summary>The game's m::snoise: the same, -1..1.</summary>
	static float SignedNoise( float x ) => 2f * Perlin( x );

	static float Perlin( float x )
	{
		float floor = MathF.Floor( x );
		int cell = (int)floor & 255;
		x -= floor;

		float fade = x * x * x * (x * (x * 6f - 15f) + 10f);
		float a = Gradient( Hash( Hash( Hash( cell ) ) ), x );
		float b = Gradient( Hash( Hash( Hash( cell + 1 ) ) ), x - 1f );
		return a + fade * (b - a);
	}

	static int Hash( int i ) => Permutation[i & 255];

	static float Gradient( int hash, float x )
	{
		int h = hash & 15;
		float u = h < 8 ? x : 0f;
		float v = h < 4 ? 0f : (h == 12 || h == 14 ? x : 0f);
		return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
	}

	// Ken Perlin's permutation, the one the game uses.
	static readonly byte[] Permutation =
	{
		151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142, 8, 99, 37, 240,
		21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88,
		237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83,
		111, 229, 122, 60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216,
		80, 73, 209, 76, 132, 187, 208, 89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186,
		3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17,
		182, 189, 28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9, 129,
		22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97, 228, 251, 34, 242, 193, 238,
		210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157, 184,
		84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195,
		78, 66, 215, 61, 156, 180,
	};
}
