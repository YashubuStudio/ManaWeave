Shader "ManaWeave/Voxel"
{
    Properties { _Survey ("Survey display", Range(0,1)) = 0 }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            float _Survey;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; fixed4 color : COLOR; };
            struct v2f { float4 position : SV_POSITION; float3 normal : TEXCOORD0; fixed4 color : COLOR; UNITY_FOG_COORDS(1) };
            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                UNITY_TRANSFER_FOG(o,o.position);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float direct = saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz)));
                float hemisphere = saturate(n.y * 0.5 + 0.5);
                float3 light = float3(0.32,0.39,0.49) + hemisphere * float3(0.14,0.17,0.18) + direct * _LightColor0.rgb * 0.62;
                light = lerp(light, float3(0.78,0.83,0.90) + hemisphere * 0.16, _Survey);
                fixed4 color = fixed4(i.color.rgb * light + i.color.rgb * i.color.a * 0.22,1);
                UNITY_APPLY_FOG(i.fogCoord, color);
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
