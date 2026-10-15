
HEADER
{
	Description = "Alan Wake Water";
}

FEATURES
{
	#include "common/features.hlsl"

	Feature(F_TRANSLUCENT, 0..1, "Rendering");
}

MODES
{
	Forward();
	Depth();
	ToolsShadingComplexity("tools_shading_complexity.shader");
}

COMMON
{
	#include "common/shared.hlsl"
	#include "procedural.hlsl"

	// ─────────────────────────────────────────────────────────────────────────
	// Flow phase. Flowing water carries its textures along for one cycle, then
	// starts over; two copies half a cycle apart are cross-faded so neither
	// restart is ever seen. A slow noise offsets the cycle across the surface so
	// the cross-fade never pulses everywhere at once.
	// ─────────────────────────────────────────────────────────────────────────
	float FlowHash(float2 p)
	{
		float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
		p3 += dot(p3, p3.yzx + 33.33);
		return frac((p3.x + p3.y) * p3.z);
	}

	float FlowPhaseNoise(float2 worldXY)
	{
		float2 p = worldXY / 750.0;
		float2 i = floor(p);
		float2 f = p - i;
		float2 u = f * f * (3.0 - 2.0 * f);

		float a = FlowHash(i);
		float b = FlowHash(i + float2(1.0, 0.0));
		float c = FlowHash(i + float2(0.0, 1.0));
		float d = FlowHash(i + float2(1.0, 1.0));

		return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"

	// The water's state at this vertex: x = flow speed along the tangent (world units/s), y = wave strength,
	// z = foam.
	float4 vColor : COLOR0 < Semantic(Color); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// xy = flow in plan (world units/s), z = wave strength, w = foam.
	float4 vColor : COLOR0;
};



VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs(VertexInput v)
	{
		PixelInput i = ProcessVertex(v);

		// The mesh's tangent points downstream; turned with the object, it gives the flow in plan.
		float2 along = i.vTangentUWs.xy;
		float alongLength = length(along);
		float2 flow = alongLength > 0.001 ? along / alongLength * v.vColor.x : float2(0.0, 0.0);
		i.vColor = float4(flow, v.vColor.y, v.vColor.z);

		// The surface in plan: the textures stay put in the world.
		i.vTextureCoords.xy = i.vPositionWs.xy;

		return FinalizeVertex(i);
	}
}



PS
{
	#include "common/pixel.hlsl"

	StaticCombo(S_TRANSLUCENT, F_TRANSLUCENT, Sys(ALL));

	RenderState(CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT);

	// Refraction and reflections read a copy of the frame drawn so far.
	BoolAttribute(bWantsFBCopyTexture, true);

	// Water never shadows what lies under it.
	BoolAttribute(DoNotCastShadows, true);

	// ── Textures ──
	Texture2D g_tFrameBufferCopyTexture < Attribute("FrameBufferCopyTexture"); SrgbRead(false); > ;

	CreateInputTexture2D(MainNormal, Linear, 8, "NormalizeNormals", "_normal", "Normals,0/,0/0", DefaultFile("materials/default/default_normal.tga"));
	CreateInputTexture2D(SecondNormal, Linear, 8, "NormalizeNormals", "_normal", "Normals,0/,0/0", DefaultFile("materials/default/default_normal.tga"));
	Texture2D g_tMainNormal < Channel( RGBA, Box( MainNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( False ); >;
	Texture2D g_tSecondNormal < Channel( RGBA, Box( SecondNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( False ); >;
	TextureAttribute( LightSim_DiffuseAlbedoTexture, g_tSecondNormal )
	TextureAttribute( RepresentativeTexture, g_tSecondNormal )

	// ── Normal scrolling ──
	float2 g_vNormalTiling < UiGroup( "Normals,0/,0/0" ); Default2( 0.01, 0.01 ); >;
	float g_flSpeedMainNormal < UiGroup( "Normals,0/,0/0" ); Default1( 50 ); Range1( -1000, 1000 ); >;
	float g_flSpeedSecondNormal < UiGroup( "Normals,0/,0/0" ); Default1( -25 ); Range1( -1000, 1000 ); >;
	float g_flNormalStrength < UiType( Slider ); UiGroup( "Normals,0/,0/0" ); Default1( 1 ); Range1( 0, 2 ); >;

	// ── Flow ──
	float g_flFlowCycle < UiGroup( "Normals,0/,0/0" ); Default1( 2 ); Range1( 0.1, 10 ); >;

	// ── Refraction ──
	float g_flRefractionStrength < UiType( Slider ); UiGroup( "Refraction,0/,0/0" ); Default1( 0.1 ); Range1( 0, 1 ); >;

	// ── Depth coloring ──
	float4 g_vShallowColor < UiType( Color ); UiGroup( "Depth,0/,0/0" ); Default4( 0.37, 1.00, 0.89, 0.57 ); >;
	float4 g_vDeepColor < UiType(Color); UiGroup("Depth,0/,0/0"); Default4(0.00, 0.88, 1.00, 0.48); > ;
	float g_flDepthMax < UiGroup("Depth,0/,0/0"); Default1(1000); Range1(1, 10000); > ;
	float g_flDepthMultiplier < UiGroup("Depth,0/,0/0"); Default1(1); Range1(0, 2); > ;
	float g_flDepthFalloff < UiGroup( "Depth,0/,0/0" ); Default1( 0.5 ); Range1( 0, 2 ); >;
	float g_flDepthBlend < UiGroup( "Depth,0/,0/0" ); Default1( 0.75 ); Range1( 0, 1 ); >;
	float g_flShoreOpacity < UiType( Slider ); UiGroup( "Depth,0/,0/0" ); Default1( 0 ); Range1( 0, 1 ); >;
	float g_flShoreOpacityRange < UiGroup( "Depth,0/,0/0" ); Default1( 100 ); Range1( 0, 1000 ); >;

	// ── Surface caustics ──
	float g_flCausticsThresholdMin < UiGroup( "Caustics,0/,0/0" ); Default1( 0.3 ); Range1( 0, 2 ); >;
	float g_flCausticsThresholdMax < UiGroup( "Caustics,0/,0/0" ); Default1( 2 ); Range1( 0, 2 ); >;
	float g_flCausticsTilingMultiplier < UiGroup( "Caustics,0/,0/0" ); Default1( 1 ); Range1( 0.1, 10 ); >;
	float g_flCausticsScrollSpeed < UiGroup( "Caustics,0/,0/0" ); Default1( 0.01 ); Range1( 0, 0.1 ); >;
	float g_flCausticsAnimSpeed < UiGroup( "Caustics,0/,0/0" ); Default1( 1 ); Range1( 0.1, 10 ); >;
	float g_flCausticsIntensity < UiGroup( "Caustics,0/,0/0" ); Default1( 0 ); Range1( 0, 1 ); >;

	// ── Foam ──
	float4 g_vFoamColor < UiType( Color ); UiGroup( "Foam,0/,0/0" ); Default4( 1.00, 1.00, 1.00, 1.00 ); >;
	float g_flFoamDepth < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 50 ); Range1( 0, 500 ); >;
	float g_flFoamFalloff < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 1.5 ); Range1( 0.1, 5 ); >;
	float g_flFoamIntensity < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.8 ); Range1( 0, 10 ); >;
	float g_flFoamNoiseScale < UiGroup( "Foam,0/,0/0" ); Default1( 20 ); Range1( 1, 100 ); >;
	float g_flFoamNoiseSpeed < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.3 ); Range1( 0, 2 ); >;
	float g_flFoamSoftness < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 1.5 ); Range1( 0, 5 ); >;
	float g_flFoamCoverage < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.5 ); Range1( 0, 5 ); >;
	float g_flFoamEdgeWarp < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.5 ); Range1( 0, 1 ); >;
	float g_flFoamEdgeScale < UiGroup( "Foam,0/,0/0" ); Default1( 5 ); Range1( 0.5, 30 ); >;
	float g_flFoamEdgeSpeed < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 0.1 ); Range1( 0, 1 ); >;

	// Foam churned up by fast water (rapids, the bottom of a fall), carried along with the current.
	float g_flFlowFoamIntensity < UiType( Slider ); UiGroup( "Foam,0/,0/0" ); Default1( 1 ); Range1( 0, 2 ); >;
	float g_flFlowFoamScale < UiGroup( "Foam,0/,0/0" ); Default1( 6 ); Range1( 0.5, 50 ); >;

	// ── Fresnel ──
	float g_flFresnelBias < UiType( Slider ); UiGroup( "Fresnel,0/,0/0" ); Default1( 0.02 ); Range1( 0, 1 ); >;
	float g_flFresnelPower < UiType( Slider ); UiGroup( "Fresnel,0/,0/0" ); Default1( 5 ); Range1( 0.5, 10 ); >;
	float4 g_vFresnelColor < UiType( Color ); UiGroup( "Fresnel,0/,0/0" ); Default4( 0.60, 0.85, 0.90, 1.00 ); >;

	// ── SSR reflection ──
	bool g_bUseScreenSpaceReflection < UiGroup("Reflection,0/,0/10"); Default1(1); > ;
	float g_flReflectionStrength < UiType( Slider ); UiGroup( "Reflection,0/,0/20" ); Default1( 0.5 ); Range1( 0.1, 1 ); >;
	float g_flFoamReflectionStrength < UiType( Slider ); UiGroup( "Reflection,0/,0/30" ); Default1( 0 ); Range1( 0, 1 ); >;
	float g_flReflectionStepSize < UiType(Slider); UiGroup("Reflection,0/,0/40"); Default1(200.0); Range1(10, 2000); > ;

	// ── Surface ──
	float g_flRoughness < UiType(Slider); UiGroup("Surface,0/,0/0"); Default1(0); Range1(0, 1); > ;
	float g_flContrast < UiType(Slider); UiGroup("Surface,0/,0/0"); Default1(1); Range1(0, 2); > ;



	// Reoriented normal mapping, simplified.
	float3 BlendNormals(float3 _A, float3 _B)
	{
		return normalize(float3(_A.xy + _B.xy, _A.z * _B.z));
	}

	float4 Hash4(float2 _P)
	{
		return frac(sin(float4(
			1.0 + dot(_P, float2(37.0, 17.0)),
			2.0 + dot(_P, float2(11.0, 47.0)),
			3.0 + dot(_P, float2(41.0, 29.0)),
			4.0 + dot(_P, float2(23.0, 31.0))
		)) * 103.0);
	}

	// Plain tiling sample: what flowing water uses. The no-tile sample below mirrors each virtual tile, which would turn
	// a directional drift round in half of them.
	float3 SampleNormalPlain(Texture2D _Tex, SamplerState _Sampler, float2 _UV)
	{
		return DecodeNormal(_Tex.Sample(_Sampler, _UV).xyz);
	}

	// No-tile sampling (Inigo Quilez): four randomly offset and flipped samples, smoothly blended, so still water never
	// shows the texture repeating.
	float3 SampleNormalNoTile(Texture2D _Tex, SamplerState _Sampler, float2 _UV)
	{
		float2 iuv = floor(_UV);
		float2 fuv = frac(_UV);

		float4 ofa = Hash4(iuv + float2(0, 0));
		float4 ofb = Hash4(iuv + float2(1, 0));
		float4 ofc = Hash4(iuv + float2(0, 1));
		float4 ofd = Hash4(iuv + float2(1, 1));

		ofa.zw = sign(ofa.zw - 0.5);
		ofb.zw = sign(ofb.zw - 0.5);
		ofc.zw = sign(ofc.zw - 0.5);
		ofd.zw = sign(ofd.zw - 0.5);

		float2 uvddx = ddx(_UV);
		float2 uvddy = ddy(_UV);

		float3 sa = DecodeNormal(_Tex.SampleGrad(_Sampler, _UV * ofa.zw + ofa.xy, uvddx * ofa.zw, uvddy * ofa.zw).xyz);
		float3 sb = DecodeNormal(_Tex.SampleGrad(_Sampler, _UV * ofb.zw + ofb.xy, uvddx * ofb.zw, uvddy * ofb.zw).xyz);
		float3 sc = DecodeNormal(_Tex.SampleGrad(_Sampler, _UV * ofc.zw + ofc.xy, uvddx * ofc.zw, uvddy * ofc.zw).xyz);
		float3 sd = DecodeNormal(_Tex.SampleGrad(_Sampler, _UV * ofd.zw + ofd.xy, uvddx * ofd.zw, uvddy * ofd.zw).xyz);

		sa.xy *= ofa.zw;
		sb.xy *= ofb.zw;
		sc.xy *= ofc.zw;
		sd.xy *= ofd.zw;

		float2 b = smoothstep(0.25, 0.75, fuv);
		return normalize(lerp(lerp(sa, sb, b.x), lerp(sc, sd, b.x), b.y));
	}

	float ColorDodge(float _Base, float _Blend)
	{
		if (_Base <= 0.0f) return 0.0f;
		if (_Blend >= 1.0f) return 1.0f;

		return saturate(_Base / (1.0f - _Blend));
	}

	float4 ColorDodge4(float4 _Base, float4 _Blend)
	{
		return float4(ColorDodge(_Base.r, _Blend.r), ColorDodge(_Base.g, _Blend.g), ColorDodge(_Base.b, _Blend.b), max(_Base.a, _Blend.a));
	}



	float4 MainPs(PixelInput i) : SV_Target0
	{
		Material m = Material::Init(i);
		m.Albedo = float3(1, 1, 1);
		m.Normal = float3(0, 0, 1);
		m.Roughness = 1;
		m.Metalness = 0;
		m.AmbientOcclusion = 1;
		m.TintMask = 1;
		m.Opacity = 1;
		m.Emission = float3(0, 0, 0);
		m.Transmission = 0;

		float3 surfacePos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;

		// The surface in plan, in world units: the textures stay put in the world.
		float2 uv = i.vTextureCoords.xy;

		float2 flow = i.vColor.xy;
		float speed = length(flow);

		// Still water drifts its normals; flowing water carries them with the current. In between the two are blended.
		float flowAmount = saturate((speed - 1.0) / 20.0);

		float phase = g_flTime / g_flFlowCycle + FlowPhaseNoise(uv);
		float phase0 = frac(phase);
		float phase1 = frac(phase + 0.5);
		float weight0 = 1.0 - abs(2.0 * phase0 - 1.0);
		float2 offset0 = flow * ((phase0 - 0.5) * g_flFlowCycle);
		float2 offset1 = flow * ((phase1 - 0.5) * g_flFlowCycle);

		float3 mainTs = float3(0, 0, 1);
		float3 secondTs = float3(0, 0, 1);

		[branch]
		if (flowAmount < 1.0)
		{
			float mainOffset = g_flTime / g_flSpeedMainNormal;
			float secondOffset = g_flTime / g_flSpeedSecondNormal;
			float2 tiledUv = uv * g_vNormalTiling;

			// Like the game's lakes: one map at several scales drifting different ways, plainly tiled. The no-tile
			// sample shows its own tile grid from afar with these maps.
			float3 mainFine = SampleNormalPlain(g_tMainNormal, g_sAniso, tiledUv + float2(mainOffset, mainOffset));
			float3 mainCoarse = SampleNormalPlain(g_tMainNormal, g_sAniso, tiledUv * 0.27 + float2(-mainOffset, mainOffset) * 0.5 + 0.31);
			float3 secondFine = SampleNormalPlain(g_tSecondNormal, g_sAniso, tiledUv * 0.61 + float2(secondOffset, -secondOffset) + 0.73);
			float3 secondCoarse = SampleNormalPlain(g_tSecondNormal, g_sAniso, tiledUv * 0.093 + float2(secondOffset, secondOffset) * 0.3 + 0.17);

			mainTs = BlendNormals(mainFine, mainCoarse);
			secondTs = BlendNormals(secondFine, secondCoarse);
		}

		[branch]
		if (flowAmount > 0.0)
		{
			// The second layer runs slower than the current, for parallax.
			float3 main0 = SampleNormalPlain(g_tMainNormal, g_sAniso, (uv - offset0) * g_vNormalTiling);
			float3 main1 = SampleNormalPlain(g_tMainNormal, g_sAniso, (uv - offset1) * g_vNormalTiling);
			float3 second0 = SampleNormalPlain(g_tSecondNormal, g_sAniso, (uv - offset0 * 0.66) * g_vNormalTiling + 0.37);
			float3 second1 = SampleNormalPlain(g_tSecondNormal, g_sAniso, (uv - offset1 * 0.66) * g_vNormalTiling + 0.37);

			mainTs = normalize(lerp(mainTs, normalize(lerp(main1, main0, weight0)), flowAmount));
			secondTs = normalize(lerp(secondTs, normalize(lerp(second1, second0, weight0)), flowAmount));
		}

		float3 mainNormal = TransformNormal(mainTs, i.vNormalWs, i.vTangentUWs, i.vTangentVWs);
		float3 secondNormal = TransformNormal(secondTs, i.vNormalWs, i.vTangentUWs, i.vTangentVWs);
		float3 blendedNormal = BlendNormals(mainNormal, secondNormal);

		float2 screenUV = CalculateViewportUv(i.vPositionSs.xy);

		// Depth at the pixel itself (used to fade the refraction only)
		float3 scenePosRaw = Depth::GetWorldPosition(i.vPositionSs.xy);
		float rawWaterDepth = max(surfacePos.z - scenePosRaw.z, 0);

		// Refraction, weaker with distance
		float cameraDist = length(surfacePos - g_vCameraPositionWs);
		float distanceScale = 1.0 / max(cameraDist * 0.01, 1.0);
		float3 refractionOffset = blendedNormal * g_flRefractionStrength * distanceScale;
		float2 distortedUV = refractionOffset.xy + screenUV;

		bool outOfBounds = any(distortedUV < 0.0) || any(distortedUV > 1.0);

		// A refracted sample landing on something above the water would leak it into the water: fall back to the
		// undistorted one.
		float2 safeSS = outOfBounds ? i.vPositionSs.xy : (distortedUV * g_vRenderTargetSize);
		float3 scenePos = Depth::GetWorldPosition(safeSS);
		bool refractedAboveWater = scenePos.z > surfacePos.z;
		float2 finalSampleUV = (refractedAboveWater || outOfBounds) ? screenUV : distortedUV;

		float4 sceneColor = g_tFrameBufferCopyTexture.Sample(g_sBilinearMirror, finalSampleUV);

		float3 finalScenePos = refractedAboveWater ? scenePosRaw : scenePos;
		float waterDepth = max(surfacePos.z - finalScenePos.z, 0);

		// ── Depth-based colour ──
		float normalizedDepth = saturate(waterDepth / (g_flDepthMax * g_flDepthMultiplier));
		float depthGradient = pow(normalizedDepth, g_flDepthFalloff);
		float4 waterColor = lerp(g_vShallowColor, g_vDeepColor, depthGradient);

		// Shallow water shows the refracted scene, deep water its own colour
		float depthBlendFactor = saturate(depthGradient * g_flDepthBlend);
		float4 refractedColor = saturate(lerp(sceneColor, waterColor, depthBlendFactor));

		// ── Surface caustics (Voronoi + colour dodge) ──
		float4 causticsColor = refractedColor;

		[branch]
		if (g_flCausticsIntensity > 0.0)
		{
			float2 causticsTiling = g_vNormalTiling * g_flCausticsTilingMultiplier;
			float causticsScrollOffset = g_flTime * g_flCausticsScrollSpeed;
			float2 causticsUV = TileAndOffsetUv(uv, causticsTiling, float2(causticsScrollOffset, causticsScrollOffset));
			float causticsNoise = VoronoiNoise(causticsUV, g_flTime * g_flCausticsAnimSpeed, 10);
			float causticsPattern = smoothstep(g_flCausticsThresholdMin, g_flCausticsThresholdMax, causticsNoise);
			causticsColor = saturate(lerp(refractedColor, ColorDodge4(refractedColor, causticsPattern.xxxx), g_flCausticsIntensity));
		}

		float3 viewDirNorm = normalize(g_vCameraPositionWs - surfacePos);

		float foamMask = 0.0;

		[branch]
		if (g_flFoamIntensity > 0.0 && waterDepth < g_flFoamDepth * (1.0 + g_flFoamEdgeWarp))
		{
			// ── Edge foam: where the water is shallow against whatever it meets ──
			float2 edgeWarpUV = uv * g_flFoamEdgeScale * g_vNormalTiling.x;
			float edgeWarpTime = g_flTime * g_flFoamEdgeSpeed;
			float edgeWarp = Simplex2D(edgeWarpUV + float2(edgeWarpTime * 0.7, edgeWarpTime * 0.4)) * 0.5 + 0.5;
			float warpedFoamDepth = g_flFoamDepth * lerp(1.0 - g_flFoamEdgeWarp, 1.0 + g_flFoamEdgeWarp, edgeWarp);

			float foamNormDepth = saturate(waterDepth / max(warpedFoamDepth, 0.001));
			float foamFalloffStart = 1.0 - (1.0 / max(g_flFoamFalloff + 1.0, 1.0));
			float foamDepthMask = 1.0 - smoothstep(foamFalloffStart, 1.0, foamNormDepth);

			float foamTime = g_flTime * g_flFoamNoiseSpeed;
			float2 foamBaseUV = uv * g_flFoamNoiseScale * g_vNormalTiling.x;

			float foamLayer1 = Simplex2D(foamBaseUV + float2(foamTime * 0.6, foamTime * 0.3)) * 0.5 + 0.5;
			float foamLayer2 = Simplex2D(foamBaseUV * 1.4 + float2(-foamTime * 0.4, foamTime * 0.5) + float2(3.7, 8.2)) * 0.5 + 0.5;
			float foamThreshold = Simplex2D(foamBaseUV * 0.2 + float2(foamTime * 0.08, -foamTime * 0.05)) * 0.1 + 0.15;
			float foamNoise = smoothstep(foamThreshold - g_flFoamSoftness, foamThreshold + g_flFoamCoverage, foamLayer1 * foamLayer2);

			// At grazing angles the depth buffer can't be trusted; fade the foam out there.
			float foamViewFade = saturate(dot(i.vNormalWs, viewDirNorm));

			foamMask = saturate(foamDepthMask * foamNoise) * g_flFoamIntensity * foamViewFade;
		}

		// ── Foam churned up by fast water, carried with the current ──
		float flowFoam = saturate(i.vColor.w);

		[branch]
		if (flowFoam > 0.001 && g_flFlowFoamIntensity > 0.0)
		{
			float2 foamScale = g_vNormalTiling * g_flFlowFoamScale;
			float2 foamUV0 = (uv - offset0) * foamScale;
			float2 foamUV1 = (uv - offset1) * foamScale;

			float pattern0 = (Simplex2D(foamUV0) * 0.5 + 0.5) * (Simplex2D(foamUV0 * 1.9 + float2(4.1, 1.7)) * 0.5 + 0.5);
			float pattern1 = (Simplex2D(foamUV1) * 0.5 + 0.5) * (Simplex2D(foamUV1 * 1.9 + float2(4.1, 1.7)) * 0.5 + 0.5);
			float pattern = lerp(pattern1, pattern0, weight0);

			// More foam lowers the threshold the pattern has to clear, so the patches grow and join up.
			float threshold = lerp(0.45, 0.05, flowFoam);
			float flowFoamMask = smoothstep(threshold, threshold + 0.2, pattern) * flowFoam * g_flFlowFoamIntensity;

			foamMask = max(foamMask, flowFoamMask);
		}

		float4 finalColor = lerp(causticsColor, g_vFoamColor, saturate(foamMask));

		// ── Fresnel (Schlick) ──
		float NdotV = saturate(dot(i.vNormalWs, viewDirNorm));
		float fresnel = g_flFresnelBias + (1.0 - g_flFresnelBias) * pow(1.0 - NdotV, g_flFresnelPower);
		finalColor = lerp(finalColor, g_vFresnelColor, fresnel);

		// ── Screen-space reflection ──
		if (g_bUseScreenSpaceReflection)
		{
			float3 lerpedNormal = normalize(lerp(i.vNormalWs, blendedNormal, g_flNormalStrength));
			float3 reflDirWs = reflect(-viewDirNorm, lerpedNormal);

			float viewDot = saturate(dot(lerpedNormal, viewDirNorm));
			float angleFactor = lerp(1.5, 0.2, viewDot);
			float heightFactor = saturate(abs(surfacePos.z - g_vCameraPositionWs.z) * 0.01);
			float stepSize = g_flReflectionStepSize * angleFactor * (0.5 + heightFactor);

			float3 virtualHit = surfacePos + reflDirWs * stepSize;
			float4 clipPos = Position3WsToPs(virtualHit);
			float2 reflUV = (clipPos.xy / clipPos.w) * 0.5 + 0.5;
			reflUV.y = 1.0 - reflUV.y;

			bool validUV = all(reflUV >= 0.0) && all(reflUV <= 1.0);
			float2 edgeDist = abs(reflUV - 0.5) * 2.0;
			float ssrWeight = validUV ? (1.0 - smoothstep(0.7, 1.0, max(edgeDist.x, edgeDist.y))) : 0.0;

			if (ssrWeight > 0.0)
			{
				float3 ssrColor = g_tFrameBufferCopyTexture.Sample(g_sPointClamp, reflUV).rgb;
				float foamReflMod = lerp(1.0, g_flFoamReflectionStrength, saturate(foamMask));
				float reflAmount = fresnel * g_flReflectionStrength * ssrWeight * foamReflMod;
				finalColor.rgb = lerp(finalColor.rgb, ssrColor, reflAmount);
			}
		}

		// ── Normal strength ──
		float3 scaledNormal = float3(
			blendedNormal.x * g_flNormalStrength,
			blendedNormal.y * g_flNormalStrength,
			lerp(1, blendedNormal.z, saturate(g_flNormalStrength))
		);

		// ── Output ──
		finalColor.rgb = saturate((finalColor.rgb - 0.5) * g_flContrast + 0.5);

		m.Albedo = finalColor.rgb;

		// Shore transparency, independent of the colour's own alpha
		float shoreBlend = saturate(rawWaterDepth / max(g_flShoreOpacityRange, 0.001));
		float shoreOpacity = lerp(g_flShoreOpacity, 1.0, shoreBlend);

		m.Opacity = saturate(shoreOpacity * waterColor.a);
		m.Normal = scaledNormal;
		m.Roughness = saturate(g_flRoughness);

		return ShadingModelStandard::Shade(m);
	}
}
