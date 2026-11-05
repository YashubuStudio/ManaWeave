Shader "ManaWeave/Sky"
{
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct a { float4 vertex:POSITION; };
            struct v { float4 position:SV_POSITION; float3 direction:TEXCOORD0; };
            v vert(a i) { v o; o.position=UnityObjectToClipPos(i.vertex); o.direction=i.vertex.xyz; return o; }
            fixed4 frag(v i):SV_Target
            {
                float3 d=normalize(i.direction);
                float elevation=saturate(d.y);
                float3 horizon=float3(0.29,0.40,0.48);
                float3 zenith=float3(0.035,0.075,0.16);
                float3 c=lerp(horizon,zenith,pow(elevation,0.42));
                c=lerp(float3(0.045,0.095,0.12),c,saturate(d.y*4+1));
                float sun=pow(saturate(dot(d,normalize(float3(-0.5,0.46,-0.65)))),280);
                c+=sun*float3(0.75,0.51,0.32);
                float band=pow(saturate(1-abs(d.y-0.25)*3),4);
                c+=band*float3(0.035,0.012,0.07);
                return fixed4(c,1);
            }
            ENDCG
        }
    }
    Fallback Off
}
