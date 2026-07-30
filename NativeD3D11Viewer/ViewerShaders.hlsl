cbuffer CBView : register(b0)
{
    float sx;
    float sy;
    float ox;
    float oy;
    float invW;
    float invH;
    float proc;
    float displayMode;
    float threshold;
    float gammaV;
    float radius;
    float blend;
    float imgW;
    float imgH;
    float pad0;
    float pad1;
};

Texture2D tex0 : register(t0);
SamplerState samp0 : register(s0);

struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct PSIn { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

PSIn VSMain(VSIn v)
{
    PSIn o;
    o.pos = float4(v.pos, 0, 1);
    o.uv = v.uv;
    return o;
}

float S(float2 uv) { return tex0.Sample(samp0, uv).r; }
float B(float2 uv) { return S(uv) >= threshold ? 1.0 : 0.0; }
float ErodeB(float2 uv, float2 t) { float m = 1; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) m = min(m, B(uv + float2(xx, yy) * t)); return m; }
float DilateB(float2 uv, float2 t) { float m = 0; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) m = max(m, B(uv + float2(xx, yy) * t)); return m; }
float ErodeOfDilateB(float2 uv, float2 t) { float m = 1; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) m = min(m, DilateB(uv + float2(xx, yy) * t, t)); return m; }
float DilateOfErodeB(float2 uv, float2 t) { float m = 0; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) m = max(m, ErodeB(uv + float2(xx, yy) * t, t)); return m; }

float3 Proc(float2 uv)
{
    float g = S(uv);
    int op = (int)(proc + 0.5);
    float p = g;
    float2 t = float2(1.0 / max(imgW, 1.0), 1.0 / max(imgH, 1.0));
    if (op == 1 || op == 7) p = g >= threshold ? 1.0 : 0.0;
    else if (op == 8)
    {
        int rr = (int)clamp(radius, 1.0, 12.0);
        float s = 0;
        float cnt = 0;
        [loop] for (int yy = -12; yy <= 12; yy++)
        {
            [loop] for (int xx = -12; xx <= 12; xx++)
            {
                if (abs(xx) <= rr && abs(yy) <= rr)
                {
                    s += S(uv + float2(xx, yy) * t);
                    cnt += 1.0;
                }
            }
        }
        float m = s / max(cnt, 1.0);
        p = g >= (m - 0.025) ? 1.0 : 0.0;
    }
    else if (op == 2) p = 1.0 - g;
    else if (op == 3) { float s = 0; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) s += S(uv + float2(xx, yy) * t); p = s / 9.0; }
    else if (op == 4) { float tl = S(uv + t * float2(-1, -1)), tc = S(uv + t * float2(0, -1)), tr = S(uv + t * float2(1, -1)); float ml = S(uv + t * float2(-1, 0)), mr = S(uv + t * float2(1, 0)); float bl = S(uv + t * float2(-1, 1)), bc = S(uv + t * float2(0, 1)), br = S(uv + t * float2(1, 1)); float gx = -tl - 2 * ml - bl + tr + 2 * mr + br; float gy = -tl - 2 * tc - tr + bl + 2 * bc + br; p = saturate(sqrt(gx * gx + gy * gy)); }
    else if (op == 5) p = saturate((g - 0.05) / 0.90);
    else if (op == 6) p = pow(saturate(g), 1.0 / max(gammaV, 0.01));
    else if (op == 9) { float a[9]; int k = 0; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) a[k++] = S(uv + float2(xx, yy) * t); [unroll] for (int i = 0; i < 9; i++) [unroll] for (int j = i + 1; j < 9; j++) if (a[j] < a[i]) { float q = a[i]; a[i] = a[j]; a[j] = q; } p = a[4]; }
    else if (op == 10) { float s = 0; [unroll] for (int yy = -1; yy <= 1; yy++) [unroll] for (int xx = -1; xx <= 1; xx++) s += S(uv + float2(xx, yy) * t); float b = s / 9.0; p = saturate(g + (g - b) * 1.8); }
    else if (op == 11) p = ErodeB(uv, t);
    else if (op == 12) p = DilateB(uv, t);
    else if (op == 13) p = DilateOfErodeB(uv, t);
    else if (op == 14) p = ErodeOfDilateB(uv, t);
    return float3(p, p, p);
}

float4 PSMain(PSIn i) : SV_TARGET
{
    float base = S(i.uv);
    float3 b = float3(base, base, base);
    float3 p = Proc(i.uv);
    if (displayMode < 0.5) return float4(b, 1);
    if (displayMode < 1.5) return float4(p, 1);
    return float4(lerp(b, p, blend), 1);
}

struct LVSIn { float2 pos : POSITION; float4 col : COLOR0; };
struct LPSIn { float4 pos : SV_POSITION; float4 col : COLOR0; };

LPSIn VSLine(LVSIn v)
{
    LPSIn o;
    o.pos = float4(v.pos, 0, 1);
    o.col = v.col;
    return o;
}

float4 PSLine(LPSIn i) : SV_TARGET
{
    return i.col;
}
