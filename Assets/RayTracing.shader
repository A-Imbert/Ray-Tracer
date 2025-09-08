Shader "Unlit/RayTracing"
{
    Properties
    {
        
        SkyColourZenith ("SkyColourZenith", Color) = (0.1, 0.3, 0.6, 1.0)
        SkyColourHorizon ("SkyColourHorizon", Color) = (0.6, 0.8, 1.0, 1.0)
        GroundColour ("GroundColour", Color) = (0.3, 0.25, 0.2, 1.0)
        SunLightDirection ("SunLightDirection", Vector) = (0.0, -1.0, 0.0) 
        SunFocus ("SunFocus", float) = 500.0
        SunIntensity ("SunIntensity", float) = 3.0
        [MaterialToggle] EnvironmentEnabled ("EnvironmentEnabled", float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // make fog work
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #pragma enable_d3d11_debug_symbols
            //
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            struct Ray {
                float3 origin;
                float3 direction;
            };
            struct RayTracingMaterial {
                float4 colour;
                float3 emissionColour;
                float emissionStrength;
                float smoothness;
            };
            struct Sphere {
                float3 position;
                float radius;
                RayTracingMaterial material;
            };
            struct Triangle {
                float3 pointA;
                float3 pointB;
                float3 pointC;

                float3 normalA;
                float3 normalB;
                float3 normalC;
            };
            struct MeshInfo {
                uint firstTriIndex;
                uint numTriangles;
                RayTracingMaterial material;
                float3 boundsMin;
                float3 boundsMax;
            };
            struct HitInfo
            {
                bool rayHit;
                float dst;
                float3 hitPoint;
                float3 normal;
                RayTracingMaterial material;
            };

            StructuredBuffer<Sphere> spheres;
            int sphereCount = 0;

            StructuredBuffer<Triangle> triangles;
            StructuredBuffer<MeshInfo> meshes;
            
            int numMeshes;
            int triCount;
            int maxBounceCount;
            float3 _PlaneParams;
            float4x4 _CamLocalToWorldMatrix;
            uint2 ScreenParams;
            int raysPerPixel;
            int _FrameCount;
            float divergeStrength;

            float4 SkyColourHorizon;
            float4 SkyColourZenith;
            float4 GroundColour; 
            float3 SunLightDirection;
            float SunFocus;
            float SunIntensity;

            //Check if a ray will hit a given sphere & returns with info
            HitInfo RaySphere(Ray ray, float3 sphereCenter, float sphereRadius){
                HitInfo hitInfo = (HitInfo)0;
                float3 sphereToRay = ray.origin - sphereCenter;
                float a = dot(ray.direction, sphereToRay);
                float b = dot(sphereToRay, sphereToRay) - sphereRadius * sphereRadius;
                float discriminant = (a * a) - b;


                //discrim: - = 0 hits 
                //discrim: 0 = 1 hit 
                //discrim: + = 2 hits
                
                if(discriminant >= 0){
                    float closerDst = -a - sqrt(discriminant);

                    //Ignore any hits behind the ray origin
                    if(closerDst >= 0){
                        hitInfo.rayHit = true;
                        hitInfo.dst = closerDst;
                        hitInfo.hitPoint = ray.origin + ray.direction * closerDst;
                        hitInfo.normal = normalize(hitInfo.hitPoint - sphereCenter);
                    }
                }
                return hitInfo;
            }
            /*Takes in the ray of the current 
                pixel and checks for the closest hit out of all the spheres */
            
            float PCGRandom(inout uint seed){
                seed = seed * 747796405 + 2891336453;
                uint x = ((seed >> ((seed >> 28) + 4)) ^ seed) * 277803737;
                x = (x >> 22) ^ x;
                // Map to [0,1)
                return x / 4294967296.0;
            }
            float3 RandomDirection(inout uint seed){
                
                for(int attempt = 0; attempt < 100; attempt++){
                    float x = PCGRandom(seed) * 2 - 1;
                    float y = PCGRandom(seed) * 2 - 1;
                    float z = PCGRandom(seed) * 2 - 1;
                    float3 pointInUnitCube = float3(x,y,z);
                    //Check if the point lies on or within the unit Sphere
                    //x^2 + y^2 + z^2 = r^2
                    float magnitudeOfPoint = dot(pointInUnitCube,pointInUnitCube);
                    if(magnitudeOfPoint <= 1.0){

                        //return the normalized direction
                        return pointInUnitCube / sqrt(magnitudeOfPoint);
                    }
                }
                return 0;
            }
            float3 RandomHemisphereDirection(float3 normal, inout uint seed){
                float3 direction = RandomDirection(seed);

                //If the dot product is negative we need to reverse the direction
                return direction * sign(dot(direction, normal));
            }
            float3 GetEnvironmentLight(Ray ray){
                float skyGradientT = pow(smoothstep(0,0.4, ray.direction.y), 0.35);
                float3 skyGradient = lerp(SkyColourHorizon.rgb, SkyColourZenith.rgb, skyGradientT);
                float sun = pow(max(0, dot(ray.direction, -SunLightDirection)), SunFocus) * SunIntensity;

                float groundToSkyT = smoothstep(-0.01, 0, ray.direction.y);
                float sunMask = groundToSkyT >= 1 ? 1.0 : 0.0;
                return lerp(GroundColour, skyGradient, groundToSkyT) + sun * sunMask;
            }
            HitInfo RayTriangle(Ray ray, Triangle tri){
                float3 edgeAB = tri.pointB - tri.pointA;
                float3 edgeAC = tri.pointC - tri.pointA;
                float3 normal = cross(edgeAB, edgeAC);
                float3 ao = ray.origin - tri.pointA;
                float3 dao = cross(ao, ray.direction);

                float determinant = -dot(ray.direction, normal);
                float invDet = 1 / determinant;

                float distance = dot(ao, normal) * invDet;
                float u = dot(edgeAC, dao) * invDet;
                float v = -dot(edgeAB, dao) * invDet;
                float w = 1 - u - v;

                HitInfo hitInfo;
                hitInfo.rayHit = determinant >= 1E-6 && distance >=0 && u >= 0 && v >= 0 && w >= 0;
                hitInfo.hitPoint = ray.origin + ray.direction * distance;
                hitInfo.normal = normalize(tri.normalA * w + tri.normalB * u + tri.normalC * v);
                hitInfo.dst = distance;
                
                return hitInfo;
            }
            bool RayBoundingBox(Ray ray, float3 minBounds, float3 maxBounds){
               float3 invDir = 1.0 / ray.direction;
               float3 t1 = (minBounds - ray.origin) * invDir;
               float3 t2 = (maxBounds - ray.origin) * invDir;
   
               float3 tMin = min(t1, t2);
               float3 tMax = max(t1, t2);
   
               float tNear = max(max(tMin.x, tMin.y), tMin.z);
               float tFar = min(min(tMax.x, tMax.y), tMax.z);
   
               return tFar >= tNear && tFar >= 0;
            }
            HitInfo CalculateRayCollision(Ray ray){
                HitInfo closestHit = (HitInfo)0;
                
                //We have no hit yet so the 'closest' hit is infinitely far away
                closestHit.dst = 1.#INF;

                for(int meshIndex = 0; meshIndex < numMeshes; meshIndex++){
                    MeshInfo meshInfo = meshes[meshIndex];

                    if(!RayBoundingBox(ray, meshInfo.boundsMin, meshInfo.boundsMax)){
                        continue;
                    }
                    for(int i = 0; i < meshInfo.numTriangles; i++){
                        int triangleIndex = meshInfo.firstTriIndex + i;
                        Triangle tri = triangles[triangleIndex];
                        HitInfo hitInfo = RayTriangle(ray, tri);

                        if(hitInfo.rayHit && hitInfo.dst < closestHit.dst){
                            closestHit = hitInfo;
                            closestHit.material = meshInfo.material;
                        }
                    }
                }
                return closestHit;
            }
            float3 Trace(Ray ray, inout uint seed)
            {
                float3 incomingLight = 0;
                float3 rayColour = 1;

                for(int i = 0; i < maxBounceCount; i++){
                    HitInfo hitInfo = CalculateRayCollision(ray);
                    if(hitInfo.rayHit){
                        //Shoot another ray out in a random direction
                        RayTracingMaterial material = hitInfo.material;
                        ray.origin = hitInfo.hitPoint;
                        float3 diffuseDir = normalize(hitInfo.normal + RandomDirection(seed));
                        float3 specularDir = reflect(ray.direction, hitInfo.normal);
                        ray.direction = lerp(diffuseDir, specularDir, material.smoothness);


                        float3 emittedLight = material.emissionColour * material.emissionStrength;
                        //Rays with a higher dot product will produce more light (head on illumination)
                        // float lightStrength = dot(hitInfo.normal, ray.direction);
                        incomingLight += emittedLight * rayColour;
                        rayColour *= material.colour;
                    }
                    else{
                        //Ray miss
                        //incomingLight += GetEnvironmentLight(ray) * rayColour;
                        break;
                    }
                }
                return incomingLight;
            }
            static const float PI = 3.1415;
            float2 RandomPointInCircle(inout uint seed){
                //Random Point on unit circle
                float angle = PCGRandom(seed) * 2 * PI;
                //Vector to that point
                float2 pointOnCircle = float2(cos(angle), sin(angle));
                //Even Spread
                return pointOnCircle * sqrt(PCGRandom(seed));
            }
            fixed4 frag (v2f i) : SV_Target
            {
                //Create seed for random number generator
                uint2 pixelCoords = uint2(i.uv.x * ScreenParams.x, i.uv.y * ScreenParams.y);
                uint seed = (pixelCoords.x * 1973 + pixelCoords.y * 9277) + _FrameCount * 33617;

                float2 uv = i.uv - 0.5;
                float3 pointLocal = float3(uv, 1) * _PlaneParams;
                float3 pointWorld = mul(_CamLocalToWorldMatrix, float4(pointLocal, 1));

                float3 camRight = float3(_CamLocalToWorldMatrix[0][0], _CamLocalToWorldMatrix[1][0], _CamLocalToWorldMatrix[2][0]);
                float3 camUp = float3(_CamLocalToWorldMatrix[0][1], _CamLocalToWorldMatrix[1][1], _CamLocalToWorldMatrix[2][1]);


                float3 totalIncomingLight = 0;
                for(int rayIndex = 0; rayIndex < raysPerPixel; rayIndex++){

                    Ray ray;
                    ray.origin = _WorldSpaceCameraPos;
                    float2 jitter = RandomPointInCircle(seed) * divergeStrength / ScreenParams.x;
                    float3 jitteredViewPoint = pointWorld + camRight * jitter.x + camUp * jitter.y;
                    ray.direction = normalize(jitteredViewPoint - ray.origin);

                    totalIncomingLight += Trace(ray, seed);
                }
                /* Right now the pixel is way too bright because it's the sum of all the rays 
                   We need to average (mean) of the light value per ray*/
                float3 pixelCol = totalIncomingLight / raysPerPixel;
                return float4(pixelCol, 1);
            }
            ENDCG
        }

        //Accumilation

        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            sampler2D _MainTex;
            sampler2D _MainTexOld;
            int _FrameCount;

            fixed4 frag(v2f i) : SV_Target
            {
                float4 prev = tex2D(_MainTexOld, i.uv);
                float4 current = tex2D(_MainTex, i.uv);

                float weight = 1.0 / (_FrameCount + 1);
                float4 accumulatedAverage = prev * (1 - weight) + current * weight;
                return accumulatedAverage;
            }
            ENDCG
        }
    }
}
