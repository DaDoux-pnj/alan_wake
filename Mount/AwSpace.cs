using System;

namespace Sandbox.Mounting;

/// <summary>
/// Alan Wake uses Direct3D conventions: left-handed, Y up, metres, clockwise front faces.
/// s&amp;box is right-handed, Z up, inches. Swapping Y/Z converts handedness (det = -1), so triangle
/// winding has to be reversed as well (same approach as the Saints Row 2 mount).
/// </summary>
static class AwSpace
{
	public const float Scale = 39.3701f;

	/// <summary>Size of one world streaming cell in metres.</summary>
	public const float CellSize = 128f;

	/// <summary>Cell (x, z) covers [ (x - CellBias) * CellSize, (x - CellBias + 1) * CellSize ) on each axis.</summary>
	public const int CellBias = 40;

	public static Vector3 Position( float x, float y, float z ) => new Vector3( x, z, y ) * Scale;
	public static Vector3 Position( Vector3 aw ) => new Vector3( aw.x, aw.z, aw.y ) * Scale;
	public static Vector3 Direction( float x, float y, float z ) => new Vector3( x, z, y );

	/// <summary>
	/// Converts a row-major 3x3 (rows = local axes in world space) into an s&amp;box rotation.
	/// With S the Y/Z swap, R = S * M^T * S, whose columns are the s&amp;box forward/left/up axes.
	/// </summary>
	public static Rotation Rotation( float[] m )
	{
		var forward = new Vector3( m[0], m[2], m[1] );
		var up = new Vector3( m[3], m[5], m[4] );
		return global::Rotation.LookAt( forward.Normal, up.Normal );
	}

	public static Transform Transform( float[] rotation, Vector3 positionMetres )
		=> new Transform( Position( positionMetres ), Rotation( rotation ) );

	public static Vector3 CellOriginMetres( int cellX, int cellZ )
		=> new Vector3( (cellX - CellBias) * CellSize, 0, (cellZ - CellBias) * CellSize );

	/// <summary>Cell centre in s&amp;box space (height 0).</summary>
	public static Vector3 CellCenter( int cellX, int cellZ )
		=> Position( CellOriginMetres( cellX, cellZ ) + new Vector3( CellSize * 0.5f, 0, CellSize * 0.5f ) );

	/// <summary>The cell an s&amp;box position (inches) lies in.</summary>
	public static (int X, int Z) CellOf( Vector3 position )
		=> ((int)MathF.Floor( position.x / Scale / CellSize ) + CellBias, (int)MathF.Floor( position.y / Scale / CellSize ) + CellBias);
}
