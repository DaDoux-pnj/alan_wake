using System;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// The game's sound definitions, read once per mount from the global database:
/// <list type="bullet">
/// <item>global/gidregistry.txt: "class,GID,name,guid" lines naming every object (class 18 = sound, 19 = simulated sound).</item>
/// <item>global/cid_sound.bin: u32 version, u32, u32 count, u32, then 83-byte records: u32 class 18, u32 gid, u8 positional,
///   u8 streamed, u8 loop, u8[3], f32 volume, f32 minDistance (m), f32 maxDistance (m), ..., u32 fileCount @72,
///   u32 files @76 (a data pool reference to fileCount archive resource ids, the FSB variations), u8[3].</item>
/// <item>global/cid_simulatedsound.bin: 32-byte records: u32 class 19, u32 gid, u32 mode, f32, u32 entryCount,
///   u32 curves, u32, u32. Their entries follow each other in cid_simulatedsoundcontent.bin (29 bytes each): u32 class 18,
///   u32 sound gid, u32, f32 weight, f32 volume, f32 minInterval (s), f32 maxInterval (s), u8 continuous. Simulated
///   sounds are the game's random ambience: birds, owls, crickets, creaks around a point.</item>
/// <item>global/dp_global.bin, the data pool: u32 a, u32 b, u32, u32, u32, (a + b) u32 table entries, then the data; a
///   reference r points at data + (r &gt;&gt; 7) * 4 (its low 7 bits are 7).</item>
/// </list>
/// </summary>
sealed class AwSoundDatabase
{
	public sealed class Sound
	{
		public uint Gid;
		public string Name;
		public bool Positional;
		public bool Loop;
		public float Volume;
		public float MinDistance;
		public float MaxDistance;
		public string[] Files;
	}

	public sealed class SimulatedEntry
	{
		public uint SoundGid;
		public float Volume;
		public float MinInterval;
		public float MaxInterval;
		public bool Continuous;
	}

	public sealed class Simulated
	{
		public uint Gid;
		public string Name;
		public SimulatedEntry[] Entries;
	}

	public readonly Dictionary<uint, Sound> Sounds = new();
	public readonly Dictionary<uint, Simulated> Simulations = new();
	/// <summary>Archive paths of the sound banks that looping sounds play: their s&amp;box sounds are made to loop.</summary>
	public readonly HashSet<string> LoopingFiles = new( StringComparer.OrdinalIgnoreCase );

	public static AwSoundDatabase Load( AwArchive archive )
	{
		var db = new AwSoundDatabase();
		var names = ReadRegistry( archive.TryRead( "global/gidregistry.txt" ) );
		var pool = archive.TryRead( "global/dp_global.bin" );
		int poolData = pool is { Length: >= 20 } ? 20 + (BitConverter.ToInt32( pool, 0 ) + BitConverter.ToInt32( pool, 4 )) * 4 : 0;

		uint[] PoolArray( uint reference, int count )
		{
			long offset = poolData + (long)(reference >> 7) * 4;
			if ( pool is null || count <= 0 || count > 256 || offset < poolData || offset + count * 4L > pool.Length )
				return Array.Empty<uint>();

			var values = new uint[count];
			for ( int i = 0; i < count; i++ )
				values[i] = BitConverter.ToUInt32( pool, (int)offset + i * 4 );
			return values;
		}

		var sounds = archive.TryRead( "global/cid_sound.bin" );
		if ( sounds is { Length: >= 16 } )
		{
			int count = BitConverter.ToInt32( sounds, 8 );
			for ( int i = 0; i < count; i++ )
			{
				int o = 16 + i * 83;
				if ( o + 83 > sounds.Length )
					break;

				var files = new List<string>();
				foreach ( var id in PoolArray( BitConverter.ToUInt32( sounds, o + 76 ), BitConverter.ToInt32( sounds, o + 72 ) ) )
				{
					if ( archive.TryGetPath( id, out var path ) && path.EndsWith( ".fsb", StringComparison.OrdinalIgnoreCase ) )
						files.Add( path );
				}

				var sound = new Sound
				{
					Gid = BitConverter.ToUInt32( sounds, o + 4 ),
					Positional = sounds[o + 8] != 0,
					Loop = sounds[o + 10] != 0,
					Volume = BitConverter.ToSingle( sounds, o + 14 ),
					MinDistance = BitConverter.ToSingle( sounds, o + 18 ),
					MaxDistance = BitConverter.ToSingle( sounds, o + 22 ),
					Files = files.ToArray(),
				};
				sound.Name = names.GetValueOrDefault( sound.Gid );
				db.Sounds[sound.Gid] = sound;
				if ( sound.Loop )
					db.LoopingFiles.UnionWith( sound.Files );
			}
		}

		var simulated = archive.TryRead( "global/cid_simulatedsound.bin" );
		var content = archive.TryRead( "global/cid_simulatedsoundcontent.bin" );
		if ( simulated is { Length: >= 16 } && content is { Length: >= 16 } )
		{
			int count = BitConverter.ToInt32( simulated, 8 );
			int next = 0;
			for ( int i = 0; i < count; i++ )
			{
				int o = 16 + i * 32;
				if ( o + 32 > simulated.Length )
					break;

				int entryCount = BitConverter.ToInt32( simulated, o + 16 );
				var entries = new List<SimulatedEntry>();
				for ( int k = 0; k < entryCount; k++, next++ )
				{
					int e = 16 + next * 29;
					if ( e + 29 > content.Length )
						break;

					entries.Add( new SimulatedEntry
					{
						SoundGid = BitConverter.ToUInt32( content, e + 4 ),
						Volume = BitConverter.ToSingle( content, e + 16 ),
						MinInterval = BitConverter.ToSingle( content, e + 20 ),
						MaxInterval = BitConverter.ToSingle( content, e + 24 ),
						Continuous = content[e + 28] == 1,
					} );
				}

				uint gid = BitConverter.ToUInt32( simulated, o + 4 );
				db.Simulations[gid] = new Simulated { Gid = gid, Name = names.GetValueOrDefault( gid ), Entries = entries.ToArray() };
			}
		}

		return db;
	}

	static Dictionary<uint, string> ReadRegistry( byte[] text )
	{
		var names = new Dictionary<uint, string>();
		if ( text is null )
			return names;

		using var reader = new StringReader( System.Text.Encoding.Latin1.GetString( text ) );
		while ( reader.ReadLine() is { } line )
		{
			var parts = line.Split( ',' );
			if ( parts.Length >= 3 && uint.TryParse( parts[1], System.Globalization.NumberStyles.HexNumber, null, out var gid ) )
				names[gid] = parts[2];
		}

		return names;
	}
}

/// <summary>
/// Sound emitters placed in world containers (cells, level files and the episodes' task files), in metres:
/// cid_soundinstance.bin and cid_simulatedsoundinstance.bin, u32 version, u32, u32 count, u32, then 69-byte records:
/// u32 type, u32 gid, f32 rotation[9], f32 position[3], u32 class (18 = sound, 19 = simulated sound), u32 sound gid,
/// u8, f32 radius (simulated sounds scatter their entries this far around the point).
/// </summary>
static class AwSoundInstances
{
	public readonly record struct Instance( Vector3 Position, uint Class, uint Gid, float Radius );

	public static void Read( Dictionary<string, byte[]> blobs, List<Instance> output )
	{
		Read( blobs.GetValueOrDefault( "cid_soundinstance.bin" ), output );
		Read( blobs.GetValueOrDefault( "cid_simulatedsoundinstance.bin" ), output );
	}

	static void Read( byte[] b, List<Instance> output )
	{
		if ( b is null || b.Length < 16 )
			return;

		int count = BitConverter.ToInt32( b, 8 );
		for ( int i = 0; i < count; i++ )
		{
			int o = 16 + i * 69;
			if ( o + 69 > b.Length )
				break;

			var position = new Vector3( BitConverter.ToSingle( b, o + 44 ), BitConverter.ToSingle( b, o + 48 ), BitConverter.ToSingle( b, o + 52 ) );
			output.Add( new Instance( position, BitConverter.ToUInt32( b, o + 56 ), BitConverter.ToUInt32( b, o + 60 ), BitConverter.ToSingle( b, o + 65 ) ) );
		}
	}
}
