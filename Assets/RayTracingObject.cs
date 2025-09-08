using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RayTracingObject : MonoBehaviour
{
    [SerializeField] public Color colour;
    [SerializeField] public Color emissionColour;
    [SerializeField, Range(0f, 1f)] public float smoothness;
    [SerializeField, Range(0f, 10f)] public float emissionStrength = 0f;
}
