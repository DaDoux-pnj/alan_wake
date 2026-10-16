//=========================================================================================================================
// Alan Wake trees, undergrowth and their billboards: s&box's foliage shader lit the way the game lights its foliage,
// diffusely only (its FoliagePRT shaders have no specular term, gloss or reflections), swaying in the game's wind.
// Instances and the wind come from AwFoliageRenderer.
//=========================================================================================================================
HEADER
{
	Description = "Alan Wake Foliage";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"

	// The game's wind data per vertex: x = branch spring / 255, y = branch weight, z = leaf spring / 255, w = leaf weight.
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	// The game's wind springs, simulated each frame by AwFoliageWind: [0, 32) small plants, [32, 64) large trees,
	// [64, 80) trunks, [80, 96) leaves. xyz: sway in object axes, w: how hard the leaves on that branch flutter.
	StructuredBuffer<float4> g_vAwWindTables < Attribute( "AwWindTables" ); >;
	float g_flAwWind < Attribute( "AwWind" ); Default( 0.0 ); >;

	// Per tree type, from the game's foliage files (set by the mount). Strength 0 (billboards) leaves it still.
	float g_flWindStrength < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Wind" ); >;
	float g_flWindPivot < Default( 0.0 ); Range( 0.0, 1000.0 ); UiGroup( "Wind" ); >;
	float g_flWindTrunkBend < Default( 0.0 ); Range( 0.0, 10.0 ); UiGroup( "Wind" ); >;
	float g_flWindLargeTable < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Wind" ); >;
	float g_flWindLeaves < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Wind" ); >;
	float g_flWindInstancePhase < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Wind" ); >;

	//
	// The game's FoliagePRT vertex wind: the trunk leans by its height above the pivot, each branch follows its spring
	// (sway in metres), and the leaves flutter on top as hard as their branch is swaying.
	//
	float3 FoliageWindOffset( float3 vPositionOs, float4 vWind, uint nPhase )
	{
		const float flInchesPerMetre = 39.3701;

		float3 vOffset = ( vPositionOs.z - g_flWindPivot ) * g_flWindTrunkBend * g_vAwWindTables[64u + nPhase].xyz;

		uint nBranch = (uint)( vWind.x * 255.0 + 0.1 ) + ( g_flWindInstancePhase > 0.5 ? nPhase : 0u );
		float4 vBranch = g_vAwWindTables[( g_flWindLargeTable > 0.5 ? 32u : 0u ) + min( nBranch, 31u )];
		vOffset += vBranch.xyz * ( vWind.y * flInchesPerMetre );

		if ( g_flWindLeaves > 0.5 )
		{
			uint nLeaf = min( (uint)( vWind.z * 255.0 + 0.1 ), 15u );
			vOffset += g_vAwWindTables[80u + nLeaf].xyz * ( vBranch.w * vWind.w * flInchesPerMetre );
		}

		return vOffset * g_flWindStrength;
	}

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );

		if ( g_flAwWind > 0.0 && g_flWindStrength > 0.0 )
		{
			float3x4 matObjectToWorld = GetTransformMatrix( i.nInstanceTransformID );
			float3 vOriginWs = mul( matObjectToWorld, float4( 0, 0, 0, 1 ) );

			// Each copy leans on one of the 16 trunk springs, picked from its position (the game uses its yaw).
			int2 vCell = (int2)floor( vOriginWs.xy / 16.0 );
			uint nPhase = ( ( (uint)vCell.x * 73856093u ) ^ ( (uint)vCell.y * 19349663u ) ) >> 7;
			nPhase = nPhase & 15u;

			float3 vPositionOs = i.vPositionOs.xyz + FoliageWindOffset( i.vPositionOs.xyz, i.vColor, nPhase );
			o.vPositionWs = mul( matObjectToWorld, float4( vPositionOs, 1.0 ) );
			o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz );
		}

		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	#if S_ALPHA_TEST
		TextureAttribute( LightSim_Opacity_A, g_tColor );
		float g_flAlphaDistanceStart < Default( 500.0 ); Range( 0.0, 5000.0 ); UiGroup( "Alpha Test" ); >;
		float g_flAlphaDistanceEnd < Default( 2000.0 ); Range( 0.0, 10000.0 ); UiGroup( "Alpha Test" ); >;

		// Relaxes the alpha cutoff with distance so thin leaves don't vanish (same as the stock foliage shader).
		float DistanceAlphaScale( float dist )
		{
			float distFactor = saturate( ( dist - g_flAlphaDistanceStart ) / max( g_flAlphaDistanceEnd - g_flAlphaDistanceStart, 0.001 ) );
			float alphaRef = lerp( g_flAlphaTestReference, 0.1, distFactor );
			return g_flAlphaTestReference / max( alphaRef, 0.001 );
		}
	#endif

	//
	// s&box's standard shading with the specular half left out, as the game has none on its foliage.
	//
	float4 ShadeDiffuseOnly( PixelInput i, Material m )
	{
		m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
		m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		m.ScreenPosition = i.vPositionSs;
		m.GeometricNormal = i.vNormalWs.xyz;

		Decals::Apply( m.WorldPosition, m );
		AdjustAlphaToCoverage( m );

		LightingTerms_t lightTerms = InitLightingTerms();
		CombinerInput combinerInput = ShadingModelStandard::MaterialToCombinerInput( m );
		ComputeDirectLighting( lightTerms, combinerInput );
		CalculateIndirectLighting( lightTerms, combinerInput );

		float3 diffuseOcclusion = CalculateDiffuseAmbientOcclusion( combinerInput, lightTerms );
		lightTerms.vIndirectDiffuse.rgb *= diffuseOcclusion;
		lightTerms.vDiffuse.rgb *= lerp( float3( 1.0, 1.0, 1.0 ), diffuseOcclusion, combinerInput.flAmbientOcclusionDirectDiffuse );

		float4 color = float4( ( lightTerms.vDiffuse.rgb + lightTerms.vIndirectDiffuse.rgb ) * combinerInput.vDiffuseColor.rgb, m.Opacity );

		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( m.Normal, m.Roughness, color.a );

		if ( g_bWireframeMode )
			return g_vWireframeColor;

		return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, color );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i );
		m.Metalness = 0;

		#if S_ALPHA_TEST
			m.Opacity *= DistanceAlphaScale( length( i.vPositionWithOffsetWs.xyz ) );
		#endif

		return ShadeDiffuseOnly( i, m );
	}
}
