Shader "Custom/WebGLPointCloud_Fixed"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _PointSize("Point Size", Float) = 10.0
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            uniform float4 _Color;
            uniform float _PointSize;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float size : PSIZE;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.size = _PointSize; // May be ignored by some WebGL platforms
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                return i.color * _Color;
            }
            ENDCG
        }
    }

    FallBack Off
}
