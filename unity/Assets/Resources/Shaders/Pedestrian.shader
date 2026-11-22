Shader "CityGenesis/Pedestrian"
{
    // 歩行者。モデルは直立の1姿勢だけで、歩く動きは頂点を関節まわりに回して作る(骨もアニメーションも持たない)。
    // 個体ごとの位置・向き・歩行の位相は _Pedestrians から SV_InstanceID で引く(PedestrianManager が毎フレーム書く)。
    // 頂点が持つ関節の情報(Y=上・Z=前方のモデル空間。手足は付け根→中間→先の3関節が直立した1本の軸に並ぶ):
    //   TEXCOORD0 = (付け根の関節の高さ, 軸の前後位置)   脚は股関節、腕は肩
    //   TEXCOORD1 = (中間の関節の高さ, 先の関節の高さ)   脚は膝と足首、腕は肘
    //   TEXCOORD2 = (付け根の関節に従う重み, 中間の関節に従う重み)
    //   TEXCOORD3 = (先の関節に従う重み, 手足の種類)     ±1=脚、±2=腕、0=胴・頭。負は位相が半周期ずれる側
    // この情報を持たないモデルは全部0と読まれ、姿勢を変えずに滑って進む。
    Properties
    {
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.15
        _Metallic("Metallic", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        // positionScale = 足元の位置と一様な拡大率。headingGait = (進む向きのX, 進む向きのZ, 歩行の位相[周期], 歩く動きの強さ)。
        // 位相は歩いた距離を1周期(2歩)の長さで割った値で、動きの強さは歩行中が1、立ち止まると0(直立)。
        struct PedestrianInstance { float4 positionScale; float4 headingGait; };
        StructuredBuffer<PedestrianInstance> _Pedestrians;
        half4 _BaseColor;
        half _Smoothness, _Metallic;
        float4x4 _PartLocal, _PartNormal;
        float _StepLength;   // 1歩の長さ(m)。位相を決めた値と同じにして、接地した足が地面を滑らないようにする
        float3 _LightDirection;

        static const float GaitLegLength = .78;      // 腰の上下動を決める脚の長さ(m)。体格ごとの差は膝の曲がりが吸収する
        static const float GaitDropTop = .005;       // 片足で立つ瞬間の腰の下がり(m)。0だと膝が伸び切る
        static const float GaitToe = .13;            // 足首からつま先の接地点までの距離(m)
        static const float GaitClearance = .05;      // 振り出す足を地面から浮かせる高さ(m)
        static const float GaitArmSwing = .35;       // 腕の振り幅(rad)
        static const float GaitArmBend = .12;        // 肘の曲げ(rad)
        static const float GaitArmBendSwing = .22;   // 腕が前へ出たときに足す肘の曲げ(rad)

        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL;
            float2 joint : TEXCOORD0; float2 lowerJoints : TEXCOORD1; float2 weights : TEXCOORD2; float2 tipLimb : TEXCOORD3; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };

        // X軸まわりに、関節より下の点が前(+Z)へ出る向きを正として回す。pivot は(高さ, 前後位置)。
        float3 Swing(float3 p, float2 pivot, float angle)
        {
            float s, c; sincos(angle, s, c);
            float2 r = float2(p.y - pivot.x, p.z - pivot.y);
            return float3(p.x, pivot.x + r.x * c + r.y * s, pivot.y + r.y * c - r.x * s);
        }

        // 腰の下がり。両足が開いて着く瞬間(位相0と0.5)に最も下がり、脚が届く高さにする。
        float BodyDrop(float phase, float amount)
        {
            float low = 1.02 * (GaitLegLength - sqrt(max(0, GaitLegLength * GaitLegLength - .25 * _StepLength * _StepLength)));
            return amount * lerp(GaitDropTop, low, .5 + .5 * cos(12.566371 * phase));
        }

        // 付け根・中間・先の関節の回転角(親の部位に対する角度)。分岐せずに脚と腕の両方を求めて選ぶ。
        float3 LimbAngles(Attributes input, float phase, float amount)
        {
            float kind = abs(input.tipLimb.y);
            float psi = frac(phase + (input.tipLimb.y < 0 ? .5 : 0));
            float forward = cos(6.2831853 * psi);
            float3 arm = amount * float3(GaitArmSwing * forward, GaitArmBend + GaitArmBendSwing * (.5 + .5 * forward), 0);
            // 脚は足首の行き先を決めて、股関節と膝の角度を2本の骨の逆運動学で解く。
            // 位相0〜0.5は接地して体の進む速さで後ろへ送り、0.5〜1は持ち上げて前へ戻す。
            float thigh = max(input.joint.x - input.lowerJoints.x, .01), shin = max(input.lowerJoints.x - input.lowerJoints.y, .01);
            float swing = (psi - .5) * 2;
            float stance = step(psi, .5);
            float reach = lerp(-.5 * _StepLength * cos(3.1415927 * swing), _StepLength * (.5 - 2 * psi), stance);
            // 足の傾き(つま先上がりが正)。かかとから着き、接地の終わりにかかとを上げ、振り出しの間に戻す。
            float foot = .25 * (1 - smoothstep(0, .08, psi)) - .7 * smoothstep(.32, .6, psi) + .95 * smoothstep(.6, 1, psi);
            float rise = sin(3.1415927 * swing);
            float lift = GaitToe * sin(max(0, -foot)) + (1 - stance) * GaitClearance * rise * rise;
            float2 toAnkle = float2(input.lowerJoints.y + amount * lift - (input.joint.x - BodyDrop(phase, amount)), amount * reach);
            float d = clamp(length(toAnkle), abs(thigh - shin) + .001, (thigh + shin) * .9995);
            float hip = atan2(toAnkle.y, -toAnkle.x) + acos(clamp((thigh * thigh + d * d - shin * shin) / (2 * thigh * d), -1, 1));
            float knee = acos(clamp((thigh * thigh + shin * shin - d * d) / (2 * thigh * shin), -1, 1)) - 3.1415927;
            float3 leg = float3(hip, knee, amount * foot - hip - knee);
            return kind < .5 ? float3(0, 0, 0) : (kind > 1.5 ? arm : leg);
        }

        Varyings PedestrianVertex(Attributes input, uint id : SV_InstanceID)
        {
            Varyings o = (Varyings)0;
            PedestrianInstance figure = _Pedestrians[id];
            float phase = figure.headingGait.z, amount = figure.headingGait.w;
            float3 p = mul(_PartLocal, float4(input.positionOS, 1)).xyz;
            float3 n = mul((float3x3)_PartNormal, input.normalOS);
            // 先の関節から順に回す。回転軸は全部X軸なので、法線は角度の合計で1回だけ回せばよい。
            float3 angles = LimbAngles(input, phase, amount) * float3(input.weights, input.tipLimb.x);
            float axis = input.joint.y;
            p = Swing(p, float2(input.lowerJoints.y, axis), angles.z);
            p = Swing(p, float2(input.lowerJoints.x, axis), angles.y);
            p = Swing(p, float2(input.joint.x, axis), angles.x);
            p.y -= BodyDrop(phase, amount);
            n = Swing(n, 0, angles.x + angles.y + angles.z);
            // モデルの +Z を進む向きへ、+X をその右へ向ける。
            float2 heading = figure.headingGait.xy;
            p *= figure.positionScale.w;
            o.positionWS = figure.positionScale.xyz + float3(heading.y * p.x + heading.x * p.z, p.y, heading.y * p.z - heading.x * p.x);
            o.normalWS = normalize(float3(heading.y * n.x + heading.x * n.z, n.y, heading.y * n.z - heading.x * n.x));
            o.positionCS = TransformWorldToHClip(o.positionWS);
            return o;
        }
        half4 PedestrianFragment(Varyings input) : SV_Target
        {
            InputData lighting = (InputData)0;
            lighting.positionWS = input.positionWS;
            lighting.normalWS = normalize(input.normalWS);
            lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            lighting.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            lighting.bakedGI = SampleSH(lighting.normalWS);
            lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            lighting.shadowMask = half4(1,1,1,1);
            SurfaceData surface = (SurfaceData)0;
            surface.albedo = _BaseColor.rgb;
            surface.metallic = _Metallic;
            surface.smoothness = _Smoothness;
            surface.normalTS = half3(0,0,1);
            surface.occlusion = 1;
            surface.alpha = 1;
            half3 color = UniversalFragmentPBR(lighting, surface).rgb;
            // Terrain/Litと同じビュー空間距離を使い、正投影でも霧の濃さを揃える。
            float fog = InitializeInputDataFog(float4(input.positionWS,1),0);
            return half4(MixFog(color,fog),1);
        }
        half4 DepthFragment(Varyings input) : SV_Target { return 0; }
        half4 NormalFragment(Varyings input) : SV_Target { return half4(normalize(input.normalWS),0); }
        half4 ShadowFragment(Varyings input) : SV_Target { return 0; }
        Varyings ShadowVertex(Attributes input, uint id : SV_InstanceID)
        {
            Varyings o = PedestrianVertex(input,id);
            o.positionCS = TransformWorldToHClip(ApplyShadowBias(o.positionWS,o.normalWS,_LightDirection));
            #if UNITY_REVERSED_Z
            o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            #else
            o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            #endif
            return o;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex PedestrianVertex
            #pragma fragment PedestrianFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly" Tags { "LightMode"="DepthOnly" } ColorMask R
            HLSLPROGRAM
            #pragma vertex PedestrianVertex
            #pragma fragment DepthFragment
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals" Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex PedestrianVertex
            #pragma fragment NormalFragment
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" } ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            ENDHLSL
        }
    }
}
