#ifndef SDF_HLSL
#define SDF_HLSL

#define MANDELBULB_ITERATIONS (15)

struct Material
{
    float3 baseColor;
    float  roughness;
    float  metalness;
    float3 pad0;
};

struct MarchingResult
{
    Material mat;
    float distance;
};

MarchingResult Blend(MarchingResult resA, MarchingResult resB, float k)
{
    float h = clamp(0.5 + 0.5 * (resB.distance - resA.distance) / k, 0.0, 1.0);

    MarchingResult result;
    result.distance      = lerp(resB.distance, resA.distance, h) - k * h * (1.0 - h);
    result.mat.baseColor = lerp(resB.mat.baseColor, resA.mat.baseColor, h);
    result.mat.roughness = lerp(resB.mat.roughness, resA.mat.roughness, h);
    result.mat.metalness = lerp(resB.mat.metalness, resA.mat.metalness, h);
    result.mat.pad0 = 0;

    return result;
}

float Sphere(float3 rayPosition, float radius)
{
    return length(rayPosition) - radius;
}

float Box(float3 rayPosition, float3 size)
{
    float3 q = abs(rayPosition) - size;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

float Torus(float3 rayPosition, float2 size)
{
    float2 q = float2(length(rayPosition.xz) - size.x, rayPosition.y);
    return length(q) - size.y;
}

float MandelBulb(float3 rayPosition, float power)
{
    float3 z          = rayPosition;
    float  dr         = 1.0;
    float  r          = 0.0;
    uint   iterations = 0;

    for (int i = 0; i < MANDELBULB_ITERATIONS; i++) {
        iterations = i;
        r = length(z);

        if (r > 2) {
            break;
        }

        // convert to polar coordinates
        float theta = acos(z.z / r);
        float phi   = atan2(z.y, z.x);
        dr = pow(r, power - 1) * power * dr + 1;

        // scale and rotate the point
        float zr = pow(r, power);
        theta = theta * power;
        phi   = phi * power;

        // convert back to cartesian coordinates
        z = zr * float3(sin(theta) * cos(phi), sin(phi) * sin(theta), cos(theta));
        z += rayPosition;
    }

    float dst = 0.5 * log(r) * r / dr;
    return dst;
    return float2(iterations, dst * 1);
}

struct SDF
{
    float4x4 worldToModel;
    float3   size;
    float    pad0;
    uint     type;
    uint     operation;
    float    blendStrength;
    float    pad1;
    float4   data;
    Material material;

    float Calculate(float3 rayPosition)
    {
        rayPosition = mul(worldToModel, float4(rayPosition, 1)).xyz;

        [call]
        switch (type)
        {
            case 0: return Sphere(rayPosition, size.x);
            case 1: return Box(rayPosition, size);
            case 2: return Torus(rayPosition, size.xy);
            case 3: return MandelBulb(rayPosition, data.x);
            default: return 1.#INF;
        }
    }

    void Combine(float3 rayPosition, inout MarchingResult currentResult)
    {
        MarchingResult thisResult;
        thisResult.distance = Calculate(rayPosition);
        thisResult.mat = material;

        [call]
        switch (operation)
        {
            case 0: //None
            {
                if (thisResult.distance < currentResult.distance)
                {
                    currentResult = thisResult;
                }
                break;
            }
            case 1: //Blend
            {
                currentResult = Blend(currentResult, thisResult, blendStrength);
                break;
            }
            case 2: //Cut
            {
                // max(a, -b)
                if (-thisResult.distance > currentResult.distance)
                {
                    currentResult = thisResult;
                    currentResult.distance = -currentResult.distance;
                }
                break;
            }
            case 3: //Mask
            {
                // max(a, b)
                if (thisResult.distance > currentResult.distance)
                {
                    currentResult = thisResult;
                }
                break;
            }
            default: break;
        }
    }
};

#endif //SDF_HLSL
