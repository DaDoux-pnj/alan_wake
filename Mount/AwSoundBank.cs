using System;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// FMOD Ex sound bank (audio/**/*.fsb, "FSB4"); Alan Wake stores one sample per file.
///   char "FSB4", u32 sampleCount, u32 sampleHeadersSize, u32 dataSize, u32 version, u32 mode, u8[24];
///   sample header (80 bytes): u16 size, char name[30], u32 lengthSamples, u32 lengthBytes, u32 loopStart, u32 loopEnd,
///     u32 mode, s32 frequency, u16 volume, s16 pan, u16 priority, u16 channels, f32 minDistance, f32 maxDistance, ...;
///   then the sample data.
/// Codecs in the game: Xbox IMA ADPCM (mode 0x400000; per channel 36-byte blocks of 64 samples, the first being the
/// block header's sample; stereo interleaves the channels every 4 bytes after both headers, more channels follow each
/// other block by block), MP3 (0x200) and 16-bit PCM (0x10).
/// </summary>
sealed class AwSoundBank
{
	const uint ModePcm16 = 0x10;
	const uint ModeMpeg = 0x200;
	const uint ModeImaAdpcm = 0x400000;

	public string Name { get; private init; }
	public int Channels { get; private init; }
	public int Rate { get; private init; }
	public int LengthSamples { get; private init; }
	public uint Mode { get; private init; }
	public byte[] Data { get; private init; }

	public bool IsMp3 => (Mode & ModeMpeg) != 0;
	public bool IsImaAdpcm => (Mode & ModeImaAdpcm) != 0;
	public bool IsPcm16 => (Mode & ModePcm16) != 0 && !IsMp3 && !IsImaAdpcm;

	public static AwSoundBank Read( byte[] b )
	{
		if ( b.Length < 128 || b[0] != 'F' || b[1] != 'S' || b[2] != 'B' || b[3] != '4' )
			throw new InvalidDataException( "Not an FSB4 sound bank" );

		int headersSize = BitConverter.ToInt32( b, 8 );
		int dataSize = BitConverter.ToInt32( b, 12 );
		const int h = 48;
		int nameLength = Array.IndexOf( b, (byte)0, h + 2, 30 ) is var end and >= 0 ? end - (h + 2) : 30;
		int lengthBytes = BitConverter.ToInt32( b, h + 36 );
		int dataStart = h + headersSize;
		int length = Math.Clamp( Math.Min( lengthBytes, dataSize ), 0, b.Length - dataStart );

		return new AwSoundBank
		{
			Name = System.Text.Encoding.Latin1.GetString( b, h + 2, nameLength ),
			LengthSamples = BitConverter.ToInt32( b, h + 32 ),
			Mode = BitConverter.ToUInt32( b, h + 48 ),
			Rate = BitConverter.ToInt32( b, h + 52 ),
			Channels = Math.Max( 1, (int)BitConverter.ToUInt16( b, h + 62 ) ),
			Data = b.AsSpan( dataStart, length ).ToArray(),
		};
	}

	static readonly int[] StepTable =
	{
		7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
		118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
		1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
		6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
	};

	static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

	/// <summary>
	/// Decodes the IMA ADPCM data to interleaved 16-bit PCM. Sounds with more than two channels (a handful of
	/// surround effects) keep their first two.
	/// </summary>
	public byte[] DecodeImaAdpcm( out int channels )
	{
		int sourceChannels = Channels;
		channels = Math.Min( sourceChannels, 2 );
		int blockSize = 36 * sourceChannels;
		int blocks = Data.Length / blockSize;
		int samples = Math.Min( LengthSamples > 0 ? LengthSamples : int.MaxValue, blocks * 64 );

		var pcm = new byte[samples * channels * 2];
		Span<short> block = stackalloc short[64];
		for ( int c = 0; c < channels; c++ )
		{
			for ( int k = 0; k < blocks; k++ )
			{
				int first = k * 64;
				if ( first >= samples )
					break;

				DecodeBlock( Data.AsSpan( k * blockSize ), sourceChannels, c, block );
				int count = Math.Min( 64, samples - first );
				for ( int i = 0; i < count; i++ )
					BitConverter.TryWriteBytes( pcm.AsSpan( ((first + i) * channels + c) * 2 ), block[i] );
			}
		}

		return pcm;
	}

	/// <summary>The 64 samples of one channel in one block: its header's sample, then 63 decoded nibbles.</summary>
	static void DecodeBlock( ReadOnlySpan<byte> b, int channels, int channel, Span<short> output )
	{
		// Mono and stereo use the interleaved layout (headers first, then 4 bytes per channel in turn); more
		// channels store one whole 36-byte block per channel.
		bool interleaved = channels <= 2;
		int header = interleaved ? channel * 4 : channel * 36;
		int sample = BitConverter.ToInt16( b.Slice( header ) );
		int index = Math.Clamp( (int)b[header + 2], 0, 88 );
		output[0] = (short)sample;

		int produced = 1;
		for ( int group = 0; group < 8 && produced < 64; group++ )
		{
			int offset = interleaved ? channels * 4 + (group * channels + channel) * 4 : header + 4 + group * 4;
			for ( int i = 0; i < 4 && produced < 64; i++ )
			{
				int value = b[offset + i];
				for ( int half = 0; half < 2 && produced < 64; half++ )
				{
					int nibble = half == 0 ? value & 15 : value >> 4;
					int step = StepTable[index];
					int diff = step >> 3;
					if ( (nibble & 1) != 0 ) diff += step >> 2;
					if ( (nibble & 2) != 0 ) diff += step >> 1;
					if ( (nibble & 4) != 0 ) diff += step;
					sample = Math.Clamp( (nibble & 8) != 0 ? sample - diff : sample + diff, short.MinValue, short.MaxValue );
					index = Math.Clamp( index + IndexTable[nibble], 0, 88 );
					output[produced++] = (short)sample;
				}
			}
		}
	}

	/// <summary>16-bit PCM data, keeping the first two channels of anything wider.</summary>
	public byte[] Pcm16( out int channels )
	{
		channels = Math.Min( Channels, 2 );
		if ( channels == Channels )
			return Data;

		int frames = Data.Length / (2 * Channels);
		var pcm = new byte[frames * channels * 2];
		for ( int f = 0; f < frames; f++ )
			Data.AsSpan( f * Channels * 2, channels * 2 ).CopyTo( pcm.AsSpan( f * channels * 2 ) );
		return pcm;
	}
}
