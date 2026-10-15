using System;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// Alan Wake texture (.tex): 32 byte header followed by the full mip chain (largest first).
///   u32 kind (0 = 2D, 2 = cube), u32 format, u32 width, u32 height, u32 depth, u32 mipCount, u32, u32
/// Formats: 1/5 = DXT1, 3/7/9 = DXT5 (9 is used for "DXT5nm" normal maps: X in alpha, Y in green),
/// 0/2/4/6/8/11 = 32 bit BGRA.
/// </summary>
static class AwTextureFile
{
	public readonly record struct Header( uint Kind, uint Format, int Width, int Height, int Mips );

	public static Header ReadHeader( byte[] b )
	{
		if ( b.Length < 32 ) throw new InvalidDataException( "Texture too small" );
		return new Header( BitConverter.ToUInt32( b, 0 ), BitConverter.ToUInt32( b, 4 ),
			BitConverter.ToInt32( b, 8 ), BitConverter.ToInt32( b, 12 ), Math.Max( 1, BitConverter.ToInt32( b, 20 ) ) );
	}

	public static bool IsDxt1( uint format ) => format is 1 or 5;
	public static bool IsDxt5( uint format ) => format is 3 or 7 or 9;
	public static bool IsCompressed( uint format ) => IsDxt1( format ) || IsDxt5( format );

	static int MipSize( uint format, int w, int h )
	{
		w = Math.Max( 1, w ); h = Math.Max( 1, h );
		if ( IsDxt1( format ) ) return Math.Max( 1, (w + 3) / 4 ) * Math.Max( 1, (h + 3) / 4 ) * 8;
		if ( IsDxt5( format ) ) return Math.Max( 1, (w + 3) / 4 ) * Math.Max( 1, (h + 3) / 4 ) * 16;
		return w * h * 4;
	}

	/// <summary>Wraps a 2D .tex in a DDS header so <see cref="TextureLoader.FromDds"/> can upload it with its own mips.</summary>
	public static byte[] ToDds( byte[] tex )
	{
		var h = ReadHeader( tex );
		if ( h.Kind != 0 )
			throw new NotSupportedException( $"Texture kind {h.Kind} (cube/volume) not supported" );

		// Clamp the mip count to what is actually present.
		int mips = 0;
		long size = 0;
		for ( int i = 0; i < h.Mips; i++ )
		{
			long s = MipSize( h.Format, h.Width >> i, h.Height >> i );
			if ( 32 + size + s > tex.Length ) break;
			size += s;
			mips++;
		}
		if ( mips == 0 ) throw new InvalidDataException( "Texture has no data" );

		var dds = new byte[128 + size];
		void W( int o, uint v ) => BitConverter.TryWriteBytes( dds.AsSpan( o ), v );
		W( 0, 0x20534444 ); // "DDS "
		W( 4, 124 );
		W( 8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 );
		W( 12, (uint)h.Height );
		W( 16, (uint)h.Width );
		W( 28, (uint)mips );
		W( 76, 32 );
		if ( IsCompressed( h.Format ) )
		{
			W( 80, 0x4 );
			W( 84, IsDxt1( h.Format ) ? 0x31545844u : 0x35545844u ); // DXT1 / DXT5
		}
		else
		{
			W( 80, 0x41 );
			W( 88, 32 );
			W( 92, 0x00FF0000 );
			W( 96, 0x0000FF00 );
			W( 100, 0x000000FF );
			W( 104, 0xFF000000 );
		}
		W( 108, 0x1000 | 0x400000 | 0x8 );
		Buffer.BlockCopy( tex, 32, dds, 128, (int)size );
		return dds;
	}

	/// <summary>
	/// Decodes one mip (the first one no larger than <paramref name="maxSize"/>) into RGBA8.
	/// </summary>
	public static byte[] DecodeRgba( byte[] tex, int maxSize, out int width, out int height )
	{
		var h = ReadHeader( tex );
		int mip = 0;
		long offset = 32;
		while ( mip < h.Mips - 1 && Math.Max( h.Width >> mip, h.Height >> mip ) > maxSize )
		{
			offset += MipSize( h.Format, h.Width >> mip, h.Height >> mip );
			mip++;
		}

		width = Math.Max( 1, h.Width >> mip );
		height = Math.Max( 1, h.Height >> mip );
		var rgba = new byte[width * height * 4];

		if ( !IsCompressed( h.Format ) )
		{
			for ( int i = 0; i < width * height; i++ )
			{
				long o = offset + i * 4;
				rgba[i * 4 + 0] = tex[o + 2];
				rgba[i * 4 + 1] = tex[o + 1];
				rgba[i * 4 + 2] = tex[o + 0];
				rgba[i * 4 + 3] = tex[o + 3];
			}
			return rgba;
		}

		bool dxt5 = IsDxt5( h.Format );
		int blockSize = dxt5 ? 16 : 8;
		int bw = Math.Max( 1, (width + 3) / 4 );
		int bh = Math.Max( 1, (height + 3) / 4 );
		Span<byte> block = stackalloc byte[64];

		for ( int by = 0; by < bh; by++ )
		for ( int bx = 0; bx < bw; bx++ )
		{
			int o = (int)offset + (by * bw + bx) * blockSize;
			DecodeColorBlock( tex.AsSpan( o + (dxt5 ? 8 : 0), 8 ), block, !dxt5 );
			if ( dxt5 ) DecodeAlphaBlock( tex.AsSpan( o, 8 ), block );

			for ( int py = 0; py < 4; py++ )
			for ( int px = 0; px < 4; px++ )
			{
				int x = bx * 4 + px, y = by * 4 + py;
				if ( x >= width || y >= height ) continue;
				block.Slice( (py * 4 + px) * 4, 4 ).CopyTo( rgba.AsSpan( (y * width + x) * 4, 4 ) );
			}
		}

		return rgba;
	}

	static void DecodeColorBlock( ReadOnlySpan<byte> b, Span<byte> outRgba, bool allowPunchThrough )
	{
		ushort c0 = (ushort)(b[0] | b[1] << 8);
		ushort c1 = (ushort)(b[2] | b[3] << 8);
		Span<byte> pal = stackalloc byte[16];
		Rgb565( c0, pal[..4] );
		Rgb565( c1, pal.Slice( 4, 4 ) );
		if ( c0 > c1 || !allowPunchThrough )
		{
			for ( int k = 0; k < 3; k++ )
			{
				pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k]) / 3);
				pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k]) / 3);
			}
			pal[11] = 255; pal[15] = 255;
		}
		else
		{
			for ( int k = 0; k < 3; k++ )
				pal[8 + k] = (byte)((pal[k] + pal[4 + k]) / 2);
			pal[11] = 255;
			pal[12] = pal[13] = pal[14] = pal[15] = 0;
		}

		uint idx = (uint)(b[4] | b[5] << 8 | b[6] << 16 | b[7] << 24);
		for ( int i = 0; i < 16; i++ )
			pal.Slice( (int)((idx >> (2 * i)) & 3) * 4, 4 ).CopyTo( outRgba.Slice( i * 4, 4 ) );
	}

	static void DecodeAlphaBlock( ReadOnlySpan<byte> b, Span<byte> outRgba )
	{
		Span<byte> a = stackalloc byte[8];
		a[0] = b[0]; a[1] = b[1];
		if ( a[0] > a[1] )
			for ( int k = 1; k < 7; k++ ) a[k + 1] = (byte)(((7 - k) * a[0] + k * a[1]) / 7);
		else
		{
			for ( int k = 1; k < 5; k++ ) a[k + 1] = (byte)(((5 - k) * a[0] + k * a[1]) / 5);
			a[6] = 0; a[7] = 255;
		}

		ulong idx = 0;
		for ( int i = 0; i < 6; i++ ) idx |= (ulong)b[2 + i] << (8 * i);
		for ( int i = 0; i < 16; i++ )
			outRgba[i * 4 + 3] = a[(int)((idx >> (3 * i)) & 7)];
	}

	static void Rgb565( ushort c, Span<byte> o )
	{
		o[0] = (byte)(((c >> 11) & 31) * 255 / 31);
		o[1] = (byte)(((c >> 5) & 63) * 255 / 63);
		o[2] = (byte)((c & 31) * 255 / 31);
		o[3] = 255;
	}
}
