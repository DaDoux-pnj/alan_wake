using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting;

/// <summary>
/// Alan Wake static/skinned mesh (.binmsh, version 19).
///
///   u32 version, u32 vertexBytes, u32 indexCount, u32 indexSize (2), u32 flags
///   vertex data, u16 indices
///   u32 boneCount, bones { str name, f32[9] rotation, f32[3] position, f32[4] sphere }
///   f32[4] sphere, f32[6] bbox, u32 lodCount
///   u32 materialCount, materials { str shader, u32 flags, u32 blend, u32 cull, u32 ?, u32 paramCount, params }
///   u32 subMeshCount, subMeshes { u32 lod, u32 vertexCount, u32 triangleCount, u32 vertexOffsetBytes,
///                                 u32 firstIndex, u32 maxBones, u8 elementCount, elements{ u8 stream, u8 type, u8 usage },
///                                 u32 paletteCount, u8[paletteCount] }
///
/// Sub-mesh N uses material N. Indices are relative to the sub-mesh's first vertex.
/// Vertex element types: 2 = float3, 4 = ubyte4 (tangent), 5 = ubyte4 (skin), 7 = short2 (uv / 4096), 8 = short4n (normal).
/// </summary>
sealed class AwMeshFile
{
	public sealed class MaterialDef
	{
		public string Shader;
		public uint Flags;
		public uint Blend;
		public uint Cull;
		public readonly Dictionary<string, string> Textures = new( StringComparer.OrdinalIgnoreCase );
		public readonly Dictionary<string, float[]> Values = new( StringComparer.OrdinalIgnoreCase );

		public bool AlphaTest => (Flags & 0x80000000u) != 0;
		public bool Translucent => Blend == 3;
		public bool TwoSided => Cull == 0;

		public string Texture( string name ) => Textures.TryGetValue( name, out var v ) ? v : null;
		public float Value( string name, float fallback ) => Values.TryGetValue( name, out var v ) && v.Length > 0 ? v[0] : fallback;
		public float[] Vector( string name ) => Values.TryGetValue( name, out var v ) ? v : null;
	}

	public sealed class SubMesh
	{
		public int Lod;
		public int MaterialIndex;
		public Vector3[] Positions;   // s&box space (inches)
		public Vector3[] Normals;     // s&box space
		public Vector2[] Uv0;
		public int[] Indices;         // s&box winding
	}

	public readonly List<MaterialDef> Materials = new();
	public readonly List<SubMesh> SubMeshes = new();
	public int LodCount = 1;

	public static AwMeshFile Parse( byte[] b )
	{
		var r = new Reader( b );
		var mesh = new AwMeshFile();

		uint version = r.U32();
		if ( version != 19 )
			throw new InvalidDataException( $"Unsupported binmsh version {version}" );

		int vertexBytes = (int)r.U32();
		int indexCount = (int)r.U32();
		int indexSize = (int)r.U32();
		r.U32();

		int vertexBase = r.Pos;
		r.Pos += vertexBytes;
		int indexBase = r.Pos;
		r.Pos += indexCount * indexSize;

		int boneCount = (int)r.U32();
		for ( int i = 0; i < boneCount; i++ )
		{
			r.Str();
			r.Pos += 16 * 4;
		}

		r.Pos += 10 * 4; // sphere + bbox
		mesh.LodCount = Math.Max( 1, (int)r.U32() );

		int materialCount = (int)r.U32();
		for ( int i = 0; i < materialCount; i++ )
		{
			var m = new MaterialDef
			{
				Shader = r.Str(),
				Flags = r.U32(),
				Blend = r.U32(),
				Cull = r.U32(),
			};
			r.U32();

			int paramCount = (int)r.U32();
			for ( int p = 0; p < paramCount; p++ )
			{
				var name = r.Str();
				uint type = r.U32();
				switch ( type )
				{
					case 7: m.Textures[name] = r.Str(); break;
					case 0: case 1: case 2: case 3:
						var v = new float[type + 1];
						for ( int k = 0; k < v.Length; k++ ) v[k] = r.F32();
						m.Values[name] = v;
						break;
					default:
						throw new InvalidDataException( $"Unknown material parameter type {type} ({name})" );
				}
			}

			mesh.Materials.Add( m );
		}

		int subCount = (int)r.U32();
		for ( int s = 0; s < subCount; s++ )
		{
			int lod = (int)r.U32();
			int vertexCount = (int)r.U32();
			int triangleCount = (int)r.U32();
			int vertexOffset = (int)r.U32();
			int firstIndex = (int)r.U32();
			r.U32();

			int elementCount = r.U8();
			var elements = new (byte Type, byte Usage)[elementCount];
			int stride = 0;
			for ( int e = 0; e < elementCount; e++ )
			{
				r.U8();
				byte type = r.U8();
				byte usage = r.U8();
				elements[e] = (type, usage);
				stride += ElementSize( type );
			}

			int paletteCount = (int)r.U32();
			r.Pos += paletteCount;

			var sub = new SubMesh
			{
				Lod = lod,
				MaterialIndex = s,
				Positions = new Vector3[vertexCount],
				Normals = new Vector3[vertexCount],
				Uv0 = new Vector2[vertexCount],
				Indices = new int[triangleCount * 3],
			};

			for ( int v = 0; v < vertexCount; v++ )
			{
				int o = vertexBase + vertexOffset + v * stride;
				bool haveUv = false;
				bool haveNormal = false;
				foreach ( var (type, usage) in elements )
				{
					if ( type == 2 && usage == 0 )
					{
						sub.Positions[v] = AwSpace.Position( BitConverter.ToSingle( b, o ), BitConverter.ToSingle( b, o + 4 ), BitConverter.ToSingle( b, o + 8 ) );
					}
					else if ( type == 8 && usage == 1 )
					{
						sub.Normals[v] = AwSpace.Direction( BitConverter.ToInt16( b, o ) / 32767f, BitConverter.ToInt16( b, o + 2 ) / 32767f, BitConverter.ToInt16( b, o + 4 ) / 32767f ).Normal;
						haveNormal = true;
					}
					else if ( type == 7 && usage == 2 && !haveUv )
					{
						sub.Uv0[v] = new Vector2( BitConverter.ToInt16( b, o ) / 4096f, BitConverter.ToInt16( b, o + 2 ) / 4096f );
						haveUv = true;
					}
					o += ElementSize( type );
				}

				if ( !haveNormal )
					sub.Normals[v] = Vector3.Up;
			}

			for ( int t = 0; t < triangleCount; t++ )
			{
				int i0 = U16( b, indexBase + (firstIndex + t * 3) * 2 );
				int i1 = U16( b, indexBase + (firstIndex + t * 3 + 1) * 2 );
				int i2 = U16( b, indexBase + (firstIndex + t * 3 + 2) * 2 );
				sub.Indices[t * 3 + 0] = i0;
				sub.Indices[t * 3 + 1] = i2;
				sub.Indices[t * 3 + 2] = i1;
			}

			mesh.SubMeshes.Add( sub );
		}

		return mesh;
	}

	static int U16( byte[] b, int o ) => b[o] | b[o + 1] << 8;

	static int ElementSize( byte type ) => type switch
	{
		2 => 12,
		4 => 4,
		5 => 4,
		7 => 4,
		8 => 8,
		_ => throw new InvalidDataException( $"Unknown vertex element type {type}" ),
	};

	internal sealed class Reader( byte[] b )
	{
		public int Pos;
		public byte U8() => b[Pos++];
		public uint U32() { uint v = BitConverter.ToUInt32( b, Pos ); Pos += 4; return v; }
		public float F32() { float v = BitConverter.ToSingle( b, Pos ); Pos += 4; return v; }
		public string Str()
		{
			int n = (int)U32();
			var s = System.Text.Encoding.Latin1.GetString( b, Pos, n ).TrimEnd( '\0' );
			Pos += n;
			return s;
		}
	}
}
