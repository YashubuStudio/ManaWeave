Shader "ManaWeave/Aura"
{
    Properties { _Tint ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            struct a { float4 vertex:POSITION; fixed4 color:COLOR; };
            struct v { float4 position:SV_POSITION; fixed4 color:COLOR; };
            v vert(a i) { v o; o.position=UnityObjectToClipPos(i.vertex); o.color=i.color*_Tint; return o; }
            fixed4 frag(v i):SV_Target { return i.color; }
            ENDCG
        }
    }
    Fallback Off
}
