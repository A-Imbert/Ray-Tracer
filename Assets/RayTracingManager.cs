using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.VisualScripting;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.UIElements;

[ExecuteAlways, ImageEffectAllowedInSceneView]
public class RayTracingManager : MonoBehaviour
{
    [SerializeField] Shader rayTracing;
    [SerializeField] Material rayTracingMaterial;
    [SerializeField] bool useSceneView;
    [SerializeField] bool useProgessiveRendering;
    [SerializeField] int maxBounceCount;
    [SerializeField] int raysPerPixel;
    [SerializeField] int framesRendered;
    [SerializeField] float divergeStrength;
    ComputeBuffer sphereBuffer;
    ComputeBuffer meshBuffer;
    ComputeBuffer triBuffer;

    RenderTexture renderOverTime;

    int lastSphereCount;
    int lastMeshCount;
    int lastTriCount;
    private void Start()
    {
        Tuple<MeshInfo[], Triangle[]> meshShaderData = GetMeshInfo();

        Debug.Log($"Mesh Count: {meshShaderData.Item1.Length}, Triangle Count: {meshShaderData.Item2.Length}");
        for (int i = 0; i < meshShaderData.Item1.Length; i++)
        {
            var mesh = meshShaderData.Item1[i];
            Debug.Log($"Mesh {i}: firstTriIndex={mesh.firstTriIndex}, boundsMin ={mesh.boundsMin}, boundsMax ={mesh.boundsMax}, smoothness = {mesh.material.smoothness}, numTriangles={mesh.numTriangles}, colour={mesh.material.colour}, emission={mesh.material.emissionStrength}");
        }
        for (int i = 0; i < Mathf.Min(meshShaderData.Item2.Length, 10); i++)
        {
            var tri = meshShaderData.Item2[i];
            Debug.Log($"Triangle {i}: A={tri.pointA}, B={tri.pointB}, C={tri.pointC} | normals A={tri.normalA}, B={tri.normalB}, C={tri.normalC}");
        }

        if (triBuffer != null) triBuffer.Release();
        if (meshBuffer != null) meshBuffer.Release();
        CreateBuffer<MeshInfo>(ref meshBuffer, meshShaderData.Item1.Length, ref lastMeshCount);
        CreateBuffer<Triangle>(ref triBuffer, meshShaderData.Item2.Length, ref lastTriCount);

        meshBuffer.SetData(meshShaderData.Item1);
        triBuffer.SetData(meshShaderData.Item2);

        rayTracingMaterial.SetBuffer("triangles", triBuffer);
        rayTracingMaterial.SetBuffer("meshes", meshBuffer);

        rayTracingMaterial.SetInt("numMeshes", meshShaderData.Item1.Length);
        rayTracingMaterial.SetInt("triCount", meshShaderData.Item2.Length);
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        UpdateRenderTexture();
        if (useSceneView) {
            
            UpdateCamera(Camera.current);


            if (useProgessiveRendering)
            {
                ProgressiveRendering(destination);
            }
            else
            {
                Graphics.Blit(source, destination, rayTracingMaterial, 0);
            }
        }
        else
        {
            Graphics.Blit(source, destination);
        }
        
    }
    void ProgressiveRendering(RenderTexture destination)
    {
        //Pass 0 --- Ray Tracer
        RenderTexture temp = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat);
        //Writes the Ray Tracing result to temp
        Graphics.Blit(null, temp, rayTracingMaterial, 0);
        if (framesRendered < 1)
        {
            Graphics.Blit(temp, renderOverTime);
        }
        else
        {
            //Pass 1 --- Accumlate with previous frame:
            RenderTexture passPrevFrame = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(renderOverTime, passPrevFrame);
            rayTracingMaterial.SetTexture("_MainTexOld", passPrevFrame);
            rayTracingMaterial.SetTexture("_MainTex", temp);
            rayTracingMaterial.SetInt("_FrameCount", framesRendered);
            Graphics.Blit(temp, renderOverTime, rayTracingMaterial, 1);
            RenderTexture.ReleaseTemporary(passPrevFrame);
        }
        RenderTexture.ReleaseTemporary(temp);
        //Final Blit to the Screen:
        Graphics.Blit(renderOverTime, destination);
        framesRendered++;
    }
    void UpdateCamera(Camera cam)
    {

        float projectionHeight = 2 * cam.nearClipPlane * Mathf.Tan((cam.fieldOfView * Mathf.Deg2Rad) / 2);
        float projectionWidth = cam.aspect * projectionHeight;
        Debug.Log(projectionHeight + " " + projectionWidth + " " + cam.nearClipPlane);

        rayTracingMaterial.SetVector("_PlaneParams", new Vector3(projectionWidth, projectionHeight, cam.nearClipPlane));
        rayTracingMaterial.SetMatrix("_CamLocalToWorldMatrix", cam.transform.localToWorldMatrix);

        rayTracingMaterial.SetFloat("divergeStrength", divergeStrength);
        rayTracingMaterial.SetInt("maxBounceCount", maxBounceCount);
        rayTracingMaterial.SetInt("raysPerPixel", raysPerPixel);
        rayTracingMaterial.SetInt("_FrameCount", framesRendered);
        rayTracingMaterial.SetVector("ScreenParams", new Vector2(Screen.width, Screen.height));
        Debug.Log("Sent To Shader");

    }
    Tuple<MeshInfo[], Triangle[]> GetMeshInfo()
    {
        GameObject[] rayTracingObjects = GameObject.FindObjectsOfType<RayTracingObject>().Select(c => c.gameObject).ToArray();
        Debug.Log($"How many Ray Tracing Objects?: {rayTracingObjects.Length}");
        MeshInfo[] meshData = new MeshInfo[rayTracingObjects.Length];
        uint currentMeshIndex = 0;
        uint totalTriangleCount = 0;
        int currentTriIndex = 0;
        foreach(GameObject obj in rayTracingObjects)
        {
            Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;
            totalTriangleCount += (uint)(mesh.triangles.Length / 3);
            Debug.Log($"totalTriangleCount: {totalTriangleCount}");

        }
        Triangle[] triData = new Triangle[totalTriangleCount];
        for (int i = 0; i < rayTracingObjects.Length; i++)
        {

            Mesh currMesh = rayTracingObjects[i].GetComponent<MeshFilter>().sharedMesh;
            Transform transform = rayTracingObjects[i].transform;
            RayTracingObject currObject = rayTracingObjects[i].GetComponent<RayTracingObject>();
            int[] triangles = currMesh.triangles;
            Vector3[] vertices = currMesh.vertices;
            Vector3[] normals = currMesh.normals;
            Vector3 boundsMin, boundsMax;
            ComputeWorldBounds(transform, currMesh.bounds, out boundsMin, out boundsMax);
            meshData[i] = new MeshInfo
            {
                firstTriIndex = (uint)currentTriIndex,
                numTriangles = (uint)(triangles.Length / 3),
                material = new RayTracingMaterial
                {
                    colour = currObject.colour,
                    emissionColour = new Vector4(currObject.emissionColour.r, currObject.emissionColour.g, currObject.emissionColour.b, 1),
                    emissionStrength = currObject.emissionStrength,
                    smoothness = currObject.smoothness
                },
                boundsMin = boundsMin,
                boundsMax = boundsMax
            };
            for(int j = 0; j < triangles.Length; j += 3)
            {
                int indexA = triangles[j];
                int indexB = triangles[j + 1];
                int indexC = triangles[j + 2];

                triData[currentTriIndex] = new Triangle
                {
                    pointA = transform.TransformPoint(vertices[indexA]),
                    pointB = transform.TransformPoint(vertices[indexB]),
                    pointC = transform.TransformPoint(vertices[indexC]),

                    normalA = transform.TransformDirection(normals[indexA]),
                    normalB = transform.TransformDirection(normals[indexB]),
                    normalC = transform.TransformDirection(normals[indexC]),
                };
                currentTriIndex++;
            }
            currentMeshIndex += (uint)(triangles.Length / 3);
        }
        return new Tuple<MeshInfo[], Triangle[]>(meshData, triData);
    }

    SphereData[] GetSphereData()
    {
        SphereCollider[] sphereColliders = FindObjectsOfType<SphereCollider>();
        SphereData[] sphereData = new SphereData[sphereColliders.Length];

        for (int i = 0; i < sphereColliders.Length; i++)
        {
            SphereCollider sphere = sphereColliders[i];
            RayTracingObject sphereScript = sphere.gameObject.GetComponent<RayTracingObject>();
            sphereData[i] = new SphereData
            {
                position = sphere.transform.position,
                radius = sphere.radius * sphere.transform.localScale.x,
                material = new RayTracingMaterial
                {
                    colour = new Vector4(sphereScript.colour.r, sphereScript.colour.g, sphereScript.colour.b, 1),
                    emissionColour = new Vector3(sphereScript.emissionColour.r, sphereScript.emissionColour.g, sphereScript.emissionColour.b),
                    smoothness = sphereScript.smoothness,
                    emissionStrength = sphereScript.emissionStrength,
                }
            };
        }
        return sphereData;
    }
    void ComputeWorldBounds(Transform t, Bounds local, out Vector3 min, out Vector3 max)
    {
        Vector3 center = local.center;
        Vector3 extents = local.extents;
        // 8 corners in local space around center
        Vector3[] corners = new Vector3[8]{
            center + new Vector3(-extents.x, -extents.y, -extents.z),
            center + new Vector3(+extents.x, -extents.y, -extents.z),
            center + new Vector3(-extents.x, +extents.y, -extents.z),
            center + new Vector3(+extents.x, +extents.y, -extents.z),
            center + new Vector3(-extents.x, -extents.y, +extents.z),
            center + new Vector3(+extents.x, -extents.y, +extents.z),
            center + new Vector3(-extents.x, +extents.y, +extents.z),
            center + new Vector3(+extents.x, +extents.y, +extents.z),
        };
        min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for(int i = 0; i < 8; i++){
            Vector3 w = t.TransformPoint(corners[i]);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
    }
    void CreateBuffer<T>(ref ComputeBuffer buffer, int count, ref int lastCount) where T : struct
    {
        if (buffer == null || count != lastCount)
        {
            if (buffer != null) buffer.Release();
            buffer = new ComputeBuffer(count, Marshal.SizeOf(typeof(T)));
            lastCount = count;
        }
    }

    void UpdateRenderTexture()
    {
        if(renderOverTime == null || renderOverTime.width != Screen.width || renderOverTime.height != Screen.height)
        {
            if (renderOverTime != null) renderOverTime.Release();
            renderOverTime = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat);
            renderOverTime.enableRandomWrite = true;
            renderOverTime.Create();
            Debug.Log($"Created new renderOverTime: {renderOverTime.width}x{renderOverTime.height}, isCreated: {renderOverTime.IsCreated()}");
            framesRendered = 0;
        }
    }
    public struct RayTracingMaterial
    {
        public Vector4 colour;
        public Vector3 emissionColour;
        public float emissionStrength;
        public float smoothness;
    };

    public struct SphereData
    {
        public Vector3 position;
        public float radius;
        public RayTracingMaterial material;
    };
    struct Triangle
    {
        public Vector3 pointA, pointB, pointC;
        public Vector3 normalA, normalB, normalC;
    };
    struct MeshInfo
    {
        public uint firstTriIndex;
        public uint numTriangles;
        public RayTracingMaterial material;
        public Vector3 boundsMin;
        public Vector3 boundsMax;
    };
    void OnDestroy()
    {
        if (sphereBuffer != null) { 
            sphereBuffer.Release(); 
            sphereBuffer = null; 
        }
        if (meshBuffer != null) { 
            meshBuffer.Release();
            meshBuffer = null; 
        }
        if (triBuffer != null) { 
            triBuffer.Release();
            triBuffer = null; 
        }
    }
}
