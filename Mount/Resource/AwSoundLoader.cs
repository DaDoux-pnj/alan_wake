using System;

namespace Sandbox.Mounting;

/// <summary>
/// One of the game's sound banks (audio/**/*.fsb) as an s&amp;box sound, mount://alan_wake/audio/....vsnd. It loops
/// when a looping sound definition plays it (the banks themselves don't say).
/// </summary>
sealed class AwSoundLoader( string archivePath ) : ResourceLoader<AlanWakeMount>
{
	protected override object Load()
	{
		try
		{
			var bank = AwSoundBank.Read( Host.Archive.Read( archivePath ) );
			bool loop = Host.GetSoundDatabase()?.LoopingFiles.Contains( archivePath ) == true;

			if ( bank.IsMp3 )
				return SoundFile.FromMp3( Path, bank.Data, new SoundFile.LoadOptions { Loop = loop } );

			byte[] pcm;
			int channels;
			if ( bank.IsImaAdpcm )
				pcm = bank.DecodeImaAdpcm( out channels );
			else if ( bank.IsPcm16 )
				pcm = bank.Pcm16( out channels );
			else
			{
				Log.Warning( $"[AW] sound '{archivePath}': unsupported format 0x{bank.Mode:x}" );
				return null;
			}

			return SoundFile.FromPcm( Path, pcm, new SoundFile.PcmOptions { Channels = channels, Rate = (uint)bank.Rate, Bits = 16, Loop = loop } );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AW] sound '{archivePath}': {ex.Message}" );
			return null;
		}
	}
}

/// <summary>
/// Picks the game's ambience out of its placed sound emitters and turns it into SoundPointComponents (engine components,
/// so they work in any game). The game's scripts switch every placed sound on and off as the story goes, so what's kept
/// is what sounds like ambience: sounds and simulated sounds (random birds, owls, creaks...) named as ambience, at
/// world positions (sounds attached to moving objects store small local positions instead), spaced out. 2D sounds
/// become local ones, since all of them playing everywhere at once is deafening. Looping sounds play on, the rest
/// repeat at the game's intervals.
/// </summary>
static class AwSoundPlacer
{
	/// <summary>A sound to place: the game's emitter, resolved and tagged "day", "night" or null.</summary>
	public sealed class Emitter
	{
		public Vector3 Position;
		public string Name;
		public AwSoundDatabase.Sound Sound;
		public float Volume;
		public float Radius;
		public bool Repeat;
		public float MinInterval;
		public float MaxInterval;
		public string Tag;
	}

	static readonly HashSet<string> Excluded = new( StringComparer.OrdinalIgnoreCase )
	{
		"music", "dialogue", "enemy", "weapons", "combat", "collisions", "foley", "vehicle", "vehicles", "hud", "menu",
		"prototype", "stingers", "debug", "kf_paths", "soundtrack",
	};

	/// <summary>The game's ambience among placed emitters (positions in Alan Wake metres).</summary>
	public static List<Emitter> Collect( AwSoundDatabase db, IEnumerable<AwSoundInstances.Instance> instances )
	{
		var emitters = new List<Emitter>();
		var placed = new Dictionary<(uint, uint), List<Vector3>>();

		foreach ( var instance in instances )
		{
			var p = instance.Position;
			if ( MathF.Abs( p.x ) < 20 && MathF.Abs( p.z ) < 20 )
				continue;

			// One copy of a sound per 20 m, and simulated sounds no closer than their own radius (they'd overlap).
			float spacing = instance.Class == 19 ? MathF.Max( 20, instance.Radius ) : 20;
			if ( !placed.TryGetValue( (instance.Class, instance.Gid), out var copies ) )
				placed[(instance.Class, instance.Gid)] = copies = new List<Vector3>();
			if ( copies.Exists( q => q.Distance( p ) < spacing ) )
				continue;

			int before = emitters.Count;
			if ( instance.Class == 19 && db.Simulations.TryGetValue( instance.Gid, out var simulated ) )
			{
				var name = simulated.Name ?? simulated.Entries.Select( e => db.Sounds.GetValueOrDefault( e.SoundGid )?.Name ).FirstOrDefault( n => n is not null );
				if ( !IsAmbience( name ) )
					continue;

				string tag = null;
				var entries = new List<Emitter>();
				foreach ( var entry in simulated.Entries )
				{
					if ( !db.Sounds.TryGetValue( entry.SoundGid, out var sound ) || !Playable( sound ) )
						continue;

					float min = entry.MinInterval, max = entry.MaxInterval;
					if ( !sound.Loop && max <= 0 ) (min, max) = (5f, 15f);
					tag = Combine( tag, TagOf( sound ) );
					entries.Add( new Emitter
					{
						Position = p, Name = name, Sound = sound, Volume = entry.Volume, Radius = instance.Radius,
						Repeat = !sound.Loop, MinInterval = min, MaxInterval = max,
					} );
				}

				foreach ( var e in entries )
					e.Tag = Combine( tag, TagOf( name ) );
				emitters.AddRange( entries );
			}
			else if ( instance.Class == 18 && db.Sounds.TryGetValue( instance.Gid, out var sound ) && Playable( sound ) && IsAmbience( sound.Name ) )
			{
				// Looping sounds play on; ambience one-shots get a guessed pause (the game's scripts time them).
				emitters.Add( new Emitter
				{
					Position = p, Name = sound.Name, Sound = sound, Volume = 1, Repeat = !sound.Loop,
					MinInterval = 10, MaxInterval = 30, Tag = TagOf( sound ),
				} );
			}

			if ( emitters.Count > before )
				copies.Add( p );
		}

		return emitters;
	}

	/// <summary>Ambience by name: ambience\..., ambient_tracks\..., or an area ambience like scripted\level09\trailerambience_....</summary>
	static bool IsAmbience( string name )
		=> name is not null && (name.StartsWith( "ambien", StringComparison.OrdinalIgnoreCase ) || name.Contains( "ambience", StringComparison.OrdinalIgnoreCase ));

	/// <summary>Has sound banks and isn't music, dialogue, combat and the like.</summary>
	static bool Playable( AwSoundDatabase.Sound sound )
	{
		if ( sound.Files.Length == 0 )
			return false;

		foreach ( var part in (sound.Name ?? "").Split( '\\', '/' ) )
		{
			if ( Excluded.Contains( part ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// "night" for owls, crickets and anything named night (by the sound or its banks, since neutral names like
	/// "birds_open_field" can play night animals), "day" for daytime sounds, else null.
	/// </summary>
	public static string TagOf( AwSoundDatabase.Sound sound )
	{
		string tag = TagOf( sound.Name );
		foreach ( var file in sound.Files )
			tag = Combine( tag, TagOf( file ) );
		return tag;
	}

	static string TagOf( string text )
	{
		if ( text is null )
			return null;
		if ( text.Contains( "night", StringComparison.OrdinalIgnoreCase ) || text.Contains( "owl", StringComparison.OrdinalIgnoreCase ) || text.Contains( "cricket", StringComparison.OrdinalIgnoreCase ) )
			return "night";
		return text.Contains( "_day", StringComparison.OrdinalIgnoreCase ) || text.Contains( "day_", StringComparison.OrdinalIgnoreCase ) ? "day" : null;
	}

	/// <summary>Night wins: a sound with any night content is a night sound.</summary>
	static string Combine( string a, string b ) => a == "night" || b == "night" ? "night" : a ?? b;

	/// <summary>Places emitters (Alan Wake metres, relative to <paramref name="parent"/>) under a new child group.</summary>
	public static int Place( GameObject parent, string groupName, IEnumerable<Emitter> emitters )
	{
		GameObject group = null;
		int added = 0;
		foreach ( var e in emitters )
		{
			// The game plays 2D emitters only inside their area: here they become local sounds that reach the
			// simulated radius, or 40 m, instead of playing everywhere at once.
			bool positional = e.Sound.Positional;
			float maxDistance = positional ? e.Sound.MaxDistance : MathF.Max( e.Radius, 40f );
			float minDistance = positional ? e.Sound.MinDistance : maxDistance * 0.25f;

			var soundEvent = EventFor( e.Sound.Files, e.Sound.Volume * e.Volume, true, minDistance, maxDistance );
			if ( soundEvent is null )
				continue;

			group ??= new GameObject { Name = groupName, Parent = parent };
			var go = new GameObject
			{
				Name = System.IO.Path.GetFileName( (e.Name ?? e.Sound.Name ?? "sound").Replace( '\\', '/' ) ),
				Parent = group,
				LocalPosition = AwSpace.Position( e.Position ),
			};

			var point = go.AddComponent<SoundPointComponent>();
			point.SoundEvent = soundEvent;
			point.PlayOnStart = true;
			if ( e.Repeat )
			{
				point.Repeat = true;
				point.MinRepeatTime = MathF.Max( e.MinInterval, 0.1f );
				point.MaxRepeatTime = MathF.Max( e.MaxInterval, point.MinRepeatTime );
			}

			added++;
		}

		return added;
	}

	/// <summary>
	/// An embedded sound event playing one of the banks at random. Positional sounds keep full volume up to the game's
	/// minimum distance and fade out at its maximum distance.
	/// </summary>
	public static SoundEvent EventFor( IReadOnlyList<string> files, float volume, bool positional, float minDistance, float maxDistance )
	{
		var sounds = new List<SoundFile>();
		foreach ( var file in files )
		{
			var sound = SoundFile.Load( AlanWakeMount.SoundPath( file ) );
			if ( sound is not null )
				sounds.Add( sound );
		}

		if ( sounds.Count == 0 )
			return null;

		float max = maxDistance > 0 ? maxDistance : 30f;
		float full = Math.Clamp( minDistance / max, 0.01f, 0.95f );
		var soundEvent = new SoundEvent
		{
			Sounds = sounds,
			Volume = volume * AlanWakeMount.SoundVolume,
			DistanceAttenuation = positional,
			Distance = max * AwSpace.Scale,
			Falloff = new Curve( new Curve.Frame( 0, 1 ), new Curve.Frame( full, 1 ), new Curve.Frame( 1, 0 ) ),
			OcclusionEnabled = false,
		};
		soundEvent.EmbeddedResource = new Sandbox.Resources.EmbeddedResource { ResourceCompiler = "embed" };
		return soundEvent;
	}
}
