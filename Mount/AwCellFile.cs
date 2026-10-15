using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Sandbox.Mounting;

/// <summary>
/// World cell containers ("HD012_032.bin", "LD012_032.bin", "global.bin", interiors, ".resources"):
///   u32 count, count x { u32 nameLength, name, u32 size }, then one zlib stream holding every blob back to back.
/// </summary>
static class AwCellFile
{
	public static Dictionary<string, byte[]> Read( byte[] data )
	{
		var result = new Dictionary<string, byte[]>( StringComparer.OrdinalIgnoreCase );
		int pos = 0;
		int count = BitConverter.ToInt32( data, pos ); pos += 4;
		var items = new List<(string Name, int Size)>( count );
		int total = 0;
		for ( int i = 0; i < count; i++ )
		{
			int len = BitConverter.ToInt32( data, pos ); pos += 4;
			var name = System.Text.Encoding.Latin1.GetString( data, pos, len ); pos += len;
			int size = BitConverter.ToInt32( data, pos ); pos += 4;
			items.Add( (name, size) );
			total += size;
		}

		var raw = new byte[total];
		using ( var input = new MemoryStream( data, pos, data.Length - pos, false ) )
		using ( var z = new ZLibStream( input, CompressionMode.Decompress ) )
		{
			int read = 0;
			while ( read < total )
			{
				int n = z.Read( raw, read, total - read );
				if ( n <= 0 ) break;
				read += n;
			}
		}

		int offset = 0;
		foreach ( var (name, size) in items )
		{
			result[name] = raw.AsSpan( offset, size ).ToArray();
			offset += size;
		}

		return result;
	}
}

/// <summary>
/// One placed static mesh. <see cref="Rotation"/> is row-major with rows being the local axes in world space
/// (Direct3D row-vector convention: world = local * M + T). Units are metres, Y up.
/// </summary>
readonly record struct AwStaticObject( float[] Rotation, Vector3 Position, uint MeshId, uint CollisionId );

static class AwStaticObjects
{
	/// <summary>cid_staticobject.bin records.</summary>
	public const int StaticRecordSize = 77;

	/// <summary>cid_dynamicobject.bin records: same leading layout, then flags, class id 100 and a GID.</summary>
	public const int DynamicRecordSize = 97;

	/// <summary>
	/// u32 version, u32 ?, u32 count, u32 0, then count fixed size records starting with
	/// f32[9] rotation, f32[3] position, u32 collision id (.binhvk), u32, u32 mesh id (.binmsh).
	/// </summary>
	public static List<AwStaticObject> Read( byte[] data, int recordSize = StaticRecordSize )
	{
		var list = new List<AwStaticObject>();
		if ( data is null || data.Length < 16 )
			return list;

		int count = BitConverter.ToInt32( data, 8 );
		if ( 16 + count * recordSize > data.Length )
			throw new InvalidDataException( $"{count} records of {recordSize} bytes don't fit in {data.Length} bytes" );

		for ( int i = 0; i < count; i++ )
		{
			int o = 16 + i * recordSize;
			var m = new float[9];
			for ( int k = 0; k < 9; k++ )
				m[k] = BitConverter.ToSingle( data, o + k * 4 );
			var t = new Vector3( BitConverter.ToSingle( data, o + 36 ), BitConverter.ToSingle( data, o + 40 ), BitConverter.ToSingle( data, o + 44 ) );
			uint collision = BitConverter.ToUInt32( data, o + 48 );
			uint mesh = BitConverter.ToUInt32( data, o + 56 );
			list.Add( new AwStaticObject( m, t, mesh, collision ) );
		}

		return list;
	}
}
