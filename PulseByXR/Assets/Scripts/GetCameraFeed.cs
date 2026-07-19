using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

public class GetCameraFeed : MonoBehaviour
{
    [Header("Passthrough Camera")]
    [Tooltip("PassthroughCameraAccess component from the 'Passthrough Camera' Building Block in this scene (Meta > Tools > Building Blocks). Requires Quest 3 / 3S.")]
    public PassthroughCameraAccess cameraAccess;

    [Tooltip("Target RawImage to display the camera feed")]
    public RawImage displayImage;

    private bool loggedNotPlaying;

    void Awake()
    {
        // PassthroughCameraAccess only waits for the permission - nothing requests it
        // on its own unless OVRManager's "Request Passthrough Camera Access Permission
        // On Startup" is enabled, so we trigger the OS permission dialog here instead.
        OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.PassthroughCameraAccess });
    }

    void Update()
    {
        if (cameraAccess == null || displayImage == null)
            return;

        if (!cameraAccess.IsPlaying)
        {
            if (!loggedNotPlaying)
            {
                loggedNotPlaying = true;
                Debug.Log("GetCameraFeed: Waiting for headset camera permission / passthrough camera to start...");
            }
            return;
        }

        loggedNotPlaying = false;

        if (displayImage.texture == null)
        {
            displayImage.texture = cameraAccess.GetTexture();
            Debug.Log("GetCameraFeed: Headset passthrough camera feed attached to RawImage.");
        }
    }

    public Texture GetCameraTexture()
    {
        return cameraAccess != null && cameraAccess.IsPlaying ? cameraAccess.GetTexture() : null;
    }

    public bool IsCameraPlaying()
    {
        return cameraAccess != null && cameraAccess.IsPlaying;
    }
}
