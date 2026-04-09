using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class XrealImageBoxSpawner : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARTrackedImageManager trackedImageManager;

    [Header("Content")]
    [SerializeField] private GameObject boxPrefab;

    [Header("Box sizing (meters)")]
    [Tooltip("Minimum width/height of the box in meters (10cm = 0.10).")]
    [SerializeField] private float minBoxSizeM = 0.10f;

    [Tooltip("How tall/deep the box should be (Z) in meters.")]
    [SerializeField] private float boxDepthM = 0.10f;

    [Header("Placement")]
    [Tooltip("Extra gap in front of the image so it doesn't intersect (meters).")]
    [SerializeField] private float frontGapM = 0.02f; // 2cm

    // One spawned box per reference image name
    private readonly Dictionary<string, GameObject> spawnedByName = new();

    private void Awake()
    {
        if (!trackedImageManager)
            trackedImageManager = GetComponent<ARTrackedImageManager>();
    }

    private void OnEnable()
    {
        trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
    }

    private void OnDisable()
    {
        trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
    }

    private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
    {
        foreach (var img in args.added) HandleImage(img);
        foreach (var img in args.updated) HandleImage(img);

        foreach (var img in args.removed)
        {
            string key = img.referenceImage.name;
            if (spawnedByName.TryGetValue(key, out var go) && go)
                go.SetActive(false);
        }
    }

    private void HandleImage(ARTrackedImage img)
    {
        string key = img.referenceImage.name;

        if (!spawnedByName.TryGetValue(key, out var box) || box == null)
        {
            box = Instantiate(boxPrefab);
            spawnedByName[key] = box;
        }

        bool isTracked = img.trackingState == TrackingState.Tracking;
        box.SetActive(isTracked);
        if (!isTracked) return;

        // 1) Size: enforce at least 10cm x 10cm, depth configurable
        float widthM = Mathf.Max(minBoxSizeM, img.size.x);
        float heightM = Mathf.Max(minBoxSizeM, img.size.y);

        // If your prefab is a Unity Cube at scale (1,1,1), this sets exact meters.
        //box.transform.localScale = new Vector3(widthM, heightM, boxDepthM);

        // 2) Position: center of image, shifted "in front" along image normal
        // Offset by half the box depth + a small gap so it sits in front.
        //float offsetM = (boxDepthM * 0.5f) + frontGapM;

        Vector3 center = img.transform.position;
        Quaternion rot = img.transform.rotation;

        // "In front of the image" = along the tracked image's forward axis.
        Vector3 inFront = center;

        box.transform.SetPositionAndRotation(inFront, rot);
    }
}