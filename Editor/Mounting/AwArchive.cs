using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Sandbox.Mounting;

/// <summary>
/// Reads Alan Wake's packed data: pairs of <c>.bin</c> (table of contents) and <c>.rmdp</c> (raw data).
///
/// TOC layout (big-endian unless the first byte is 0):
///   u8 endian, u32 version (2), u32 folderCount, u32 fileCount, u32 namesSize,
///   128 bytes of (uninitialised) base-path buffer,
///   folders[folderCount] : hash, nextSibling, parent, flags, nameOffset, firstChild, firstFile   (7 x u32)
///   files[fileCount]     : hash, next, folder, flags, nameOffset, u64 offset, u64 size, u32 crc   (40 bytes)
///   names[namesSize]     : zero terminated strings
///
/// Data is stored uncompressed. Later archives override earlier ones.
/// Every resource is also addressable by "resource id" = CRC32 of "data\&lt;path with backslashes&gt;".
/// </summary>
sealed class AwArchive : IDisposable
{
	internal readonly record struct Entry( int Pack, long Offset, long Size );

	readonly List<SafeFileHandle> _packs = new();
	readonly Dictionary<string, Entry> _files = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<uint, string> _pathByRid = new();

	public IReadOnlyCollection<string> Paths => _files.Keys;
	public int Count => _files.Count;

	public static AwArchive Open( string dataDirectory )
	{
		var archive = new AwArchive();

		// Main archive first, then numbered patch archives. Language packs ("-en", "-fr", ...) only hold
		// localized audio/text, so they are skipped.
		var tocs = new List<string>( System.IO.Directory.GetFiles( dataDirectory, "*.bin", SearchOption.TopDirectoryOnly ) );
		tocs.RemoveAll( x => Path.GetFileNameWithoutExtension( x ).Split( '-' ).Length != 2 );
		tocs.Sort( StringComparer.OrdinalIgnoreCase );

		foreach ( var toc in tocs )
		{
			var rmdp = Path.ChangeExtension( toc, ".rmdp" );
			if ( !File.Exists( rmdp ) )
				continue;

			archive.AddPack( toc, rmdp );
		}

		return archive;
	}

	void AddPack( string tocPath, string rmdpPath )
	{
		var b = File.ReadAllBytes( tocPath );
		bool bigEndian = b[0] == 1;

		uint U32( int o ) => bigEndian
			? (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3])
			: BitConverter.ToUInt32( b, o );
		ulong U64( int o ) => bigEndian ? ((ulong)U32( o ) << 32) | U32( o + 4 ) : BitConverter.ToUInt64( b, o );

		int folderCount = (int)U32( 5 );
		int fileCount = (int)U32( 9 );
		int namesSize = (int)U32( 13 );

		int folderBase = 0x11 + 0x80;
		int fileBase = folderBase + folderCount * 28;
		int nameBase = fileBase + fileCount * 40;
		if ( nameBase + namesSize > b.Length )
			throw new InvalidDataException( $"Corrupt TOC '{tocPath}'" );

		string Name( uint offset )
		{
			if ( offset == uint.MaxValue || offset >= namesSize ) return "";
			int start = nameBase + (int)offset;
			int end = start;
			while ( end < nameBase + namesSize && b[end] != 0 ) end++;
			return System.Text.Encoding.Latin1.GetString( b, start, end - start );
		}

		var folderPaths = new string[folderCount];
		for ( int i = 0; i < folderCount; i++ )
		{
			var parts = new List<string>();
			uint j = (uint)i;
			int guard = 0;
			while ( j != uint.MaxValue && j < folderCount && guard++ < 64 )
			{
				int fo = folderBase + (int)j * 28;
				var n = Name( U32( fo + 16 ) );
				if ( n.Length > 0 ) parts.Add( n );
				j = U32( fo + 8 );
			}
			parts.Reverse();
			folderPaths[i] = string.Join( '/', parts );
		}

		int packIndex = _packs.Count;
		_packs.Add( File.OpenHandle( rmdpPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess ) );

		for ( int i = 0; i < fileCount; i++ )
		{
			int fo = fileBase + i * 40;
			uint folder = U32( fo + 8 );
			var name = Name( U32( fo + 16 ) );
			long offset = (long)U64( fo + 20 );
			long size = (long)U64( fo + 28 );

			var dir = folder < folderCount ? folderPaths[folder] : "";
			var path = (dir.Length > 0 ? dir + "/" + name : name).ToLowerInvariant();

			_files[path] = new Entry( packIndex, offset, size );
			_pathByRid[ResourceId( path )] = path;
		}
	}

	public bool Exists( string path ) => _files.ContainsKey( NormalizePath( path ) );

	public bool TryGetPath( uint resourceId, out string path ) => _pathByRid.TryGetValue( resourceId, out path );

	public byte[] Read( string path )
	{
		if ( !_files.TryGetValue( NormalizePath( path ), out var e ) )
			throw new FileNotFoundException( $"'{path}' is not in the Alan Wake archives" );

		var buffer = GC.AllocateUninitializedArray<byte>( (int)e.Size );
		int read = 0;
		while ( read < buffer.Length )
		{
			int n = RandomAccess.Read( _packs[e.Pack], buffer.AsSpan( read ), e.Offset + read );
			if ( n <= 0 ) throw new EndOfStreamException( path );
			read += n;
		}
		return buffer;
	}

	public byte[] TryRead( string path ) => Exists( path ) ? Read( path ) : null;

	/// <summary>Reads only the first <paramref name="count"/> bytes of a file (headers). Null if missing.</summary>
	public byte[] ReadPrefix( string path, int count )
	{
		if ( !_files.TryGetValue( NormalizePath( path ), out var e ) )
			return null;

		var buffer = new byte[(int)Math.Min( count, e.Size )];
		int read = 0;
		while ( read < buffer.Length )
		{
			int n = RandomAccess.Read( _packs[e.Pack], buffer.AsSpan( read ), e.Offset + read );
			if ( n <= 0 ) break;
			read += n;
		}
		return buffer;
	}

	/// <summary>
	/// Material/terrain files reference textures as "runtimedata\pc\data\textures\...". Map those
	/// (and "data\..." forms) to archive paths.
	/// </summary>
	public static string NormalizePath( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return "";
		var p = path.Replace( '\\', '/' ).ToLowerInvariant();
		int i = p.IndexOf( "pc/data/", StringComparison.Ordinal );
		if ( i >= 0 ) p = p[(i + 8)..];
		else if ( p.StartsWith( "data/", StringComparison.Ordinal ) ) p = p[5..];
		return p.TrimStart( '/' );
	}

	public static uint ResourceId( string path )
	{
		var s = "data\\" + path.Replace( '/', '\\' ).ToLowerInvariant();
		uint crc = 0xFFFFFFFF;
		foreach ( char ch in s )
			crc = CrcTable[(crc ^ (byte)ch) & 0xFF] ^ (crc >> 8);
		return ~crc;
	}

	static readonly uint[] CrcTable = BuildCrcTable();

	static uint[] BuildCrcTable()
	{
		var table = new uint[256];
		for ( uint i = 0; i < 256; i++ )
		{
			uint c = i;
			for ( int k = 0; k < 8; k++ )
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			table[i] = c;
		}
		return table;
	}

	public void Dispose()
	{
		foreach ( var h in _packs )
			h.Dispose();
		_packs.Clear();
		_files.Clear();
		_pathByRid.Clear();
	}
}
