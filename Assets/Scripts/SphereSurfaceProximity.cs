using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class SphereSurfaceProximity : MonoBehaviour
{
    [Header("AR")]
    [SerializeField] private ARPlaneManager planeManager;

    [Header("Detection")]
    [Tooltip("Meters. If the sphere is within this distance to any detected plane, it turns detected.")]
    [SerializeField] private float proximityThreshold = 0.05f;

    [Header("Visuals")]
    [SerializeField] private Renderer sphereRenderer;
    [SerializeField] private Material defaultMaterial;
    [SerializeField] private Material detectedMaterial;

    private void Awake()
    {
        if (!sphereRenderer) sphereRenderer = GetComponent<Renderer>();

        // Try to auto-find ARPlaneManager if not assigned
        if (!planeManager)
        {
            planeManager = FindFirstObjectByType<ARPlaneManager>();
        }
    }

    private void Update()
    {
        if (!planeManager || sphereRenderer == null || defaultMaterial == null || detectedMaterial == null)
        {
            // Fail safe: keep default
            if (sphereRenderer && defaultMaterial) sphereRenderer.sharedMaterial = defaultMaterial;
            return;
        }

        bool nearAnyPlane = IsNearAnyDetectedPlane(transform.position, proximityThreshold);

        sphereRenderer.sharedMaterial = nearAnyPlane ? detectedMaterial : defaultMaterial;
    }

    private bool IsNearAnyDetectedPlane(Vector3 worldPos, float thresholdMeters)
    {
        foreach (var plane in planeManager.trackables)
        {
            // Skip if plane isn't currently trackable enough
            if (plane.trackingState != TrackingState.Tracking)
                continue;

            // Distance from point to plane in world space:
            // ARPlane's transform.up is the plane normal.
            Vector3 planeNormal = plane.transform.up;
            Vector3 planePoint = plane.transform.position;

            float signedDistance = Vector3.Dot(planeNormal, (worldPos - planePoint));
            float absDistance = Mathf.Abs(signedDistance);

            // First check: close to the infinite plane
            if (absDistance > thresholdMeters)
                continue;

            // Second check: within the plane polygon boundary (approx)
            // Project point onto plane and check against boundary in plane-local space
            Vector3 projected = worldPos - planeNormal * signedDistance;

            // Convert to plane local space
            Vector3 local = plane.transform.InverseTransformPoint(projected);
            var boundary = plane.boundary; // NativeArray<Vector2> in plane-space XZ-ish

            if (boundary.Length < 3)
                return true; // no boundary available; still count as near

            // local.x and local.z correspond to boundary x/y depending on ARPlane orientation
            Vector2 p = new Vector2(local.x, local.z);

            if (PointInPolygon(p, boundary))
                return true;
        }

        return false;
    }

    // Basic 2D point-in-polygon (ray casting)
    private bool PointInPolygon(Vector2 p, Unity.Collections.NativeArray<Vector2> poly)
    {
        bool inside = false;
        int j = poly.Length - 1;

        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 pi = poly[i];
            Vector2 pj = poly[j];

            bool intersect = ((pi.y > p.y) != (pj.y > p.y)) &&
                             (p.x < (pj.x - pi.x) * (p.y - pi.y) / (pj.y - pi.y + 1e-6f) + pi.x);

            if (intersect) inside = !inside;
            j = i;
        }

        return inside;
    }
}