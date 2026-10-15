HEADER
{
	Description = "Alan Wake Terrain";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 0
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif

	#include "common/shared.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vLayerUv01 : TEXCOORD4 < Semantic( None ); >;
	float4 vLayerUv2Blend : TEXCOORD5 < Semantic( None ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 vLayerUv01 : TEXCOORD14;
	float4 vLayerUv2Blend : TEXCOORD15;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vLayerUv01 = i.vLayerUv01;
		o.vLayerUv2Blend = i.vLayerUv2Blend;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	CreateInputTexture2D( Layer0, Srgb, 8, "None", "_color", "Layers,1/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tLayer0 < Channel( RGBA, Box( Layer0 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( True ); >;

	CreateInputTexture2D( Layer1, Srgb, 8, "None", "_color", "Layers,1/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tLayer1 < Channel( RGBA, Box( Layer1 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( True ); >;

	CreateInputTexture2D( Layer2, Srgb, 8, "None", "_color", "Layers,1/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tLayer2 < Channel( RGBA, Box( Layer2 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( True ); >;

	CreateInputTexture2D( BlendMap, Linear, 8, "None", "_mask", "Layers,1/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tBlend < Channel( RGBA, Box( BlendMap ), Linear ); OutputFormat( DXT5 ); SrgbRead( False ); >;

	SamplerState g_sBlendSampler < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	float g_flBrightness < UiGroup( "Layers,1/,0/0" ); Default1( 1.0 ); Range1( 0, 4 ); >;
	float g_flRoughness < UiGroup( "Layers,1/,0/0" ); Default1( 0.85 ); Range1( 0, 1 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 baseLayer = Tex2DS( g_tLayer0, g_sAniso, i.vLayerUv01.xy );
		float4 middleLayer = Tex2DS( g_tLayer1, g_sAniso, i.vLayerUv01.zw );
		float4 topLayer = Tex2DS( g_tLayer2, g_sAniso, i.vLayerUv2Blend.xy );
		float4 blendWeights = Tex2DS( g_tBlend, g_sBlendSampler, i.vLayerUv2Blend.zw );

		float middleFade = saturate( ( middleLayer.a - blendWeights.x ) * 32.0 );
		float topFade = saturate( ( topLayer.a - blendWeights.y ) * 32.0 );
		float3 lowerMix = lerp( middleLayer.rgb, baseLayer.rgb, middleFade );
		float3 terrainColor = lerp( topLayer.rgb, lowerMix, topFade ) * blendWeights.z * g_flBrightness;

		Material m = Material::Init();
		m.Albedo = terrainColor;
		m.Normal = normalize( i.vNormalWs.xyz );
		m.Roughness = g_flRoughness;
		m.Metalness = 0;
		m.AmbientOcclusion = 1;
		m.TintMask = 1;
		m.Opacity = 1;
		m.Emission = float3( 0, 0, 0 );
		m.Transmission = 0;

		return ShadingModelStandard::Shade( i, m );
	}
}
