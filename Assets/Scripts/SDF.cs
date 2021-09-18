using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SDF : MonoBehaviour
{
    public enum Type
    {
        Sphere = 0,
        Box,
        Torus,
        Mandelbulb
    }

    public enum Operation
    {
        None = 0,
        Blend,
        Cut,
        Mask
    }

    [System.Serializable]
    public class Material
    {
        public Color color = Color.white;
        public float roughness = 1.0f;
        public float metalness = 0.0f;
    }

    public Type type;
    public Operation operation;
    public Material material = new();
    public Vector4 data; //Used for additional data with specific SDFs

    [Range(0, 1)]
    public float blendStrength;
}
