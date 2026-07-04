using UnityEngine;
using UnityEngine.UI;

public class GetCameraFeed : MonoBehaviour
{
    [Header("Camera Settings")]
    [Tooltip("Target RawImage to display the camera feed")]
    public RawImage displayImage;
    
    [Tooltip("Preferred camera resolution width")]
    public int requestedWidth = 1280;
    
    [Tooltip("Preferred camera resolution height")]
    public int requestedHeight = 720;
    
    [Tooltip("Preferred framerate")]
    public int requestedFPS = 30;
    
    [Tooltip("Use front camera if available")]
    public bool useFrontCamera = true;

    private WebCamTexture webCamTexture;
    private bool isCameraInitialized = false;

    void Start()
    {
        InitializeCamera();
    }

    void InitializeCamera()
    {
        // Check if any cameras are available
        WebCamDevice[] devices = WebCamTexture.devices;
        
        if (devices.Length == 0)
        {
            Debug.LogError("GetCameraFeed: No camera devices found!");
            return;
        }

        // Log available cameras
        Debug.Log($"GetCameraFeed: Found {devices.Length} camera(s):");
        for (int i = 0; i < devices.Length; i++)
        {
            Debug.Log($"  [{i}] {devices[i].name} (Front: {devices[i].isFrontFacing})");
        }

        // Select the appropriate camera
        string selectedCameraName = null;
        foreach (WebCamDevice device in devices)
        {
            if (useFrontCamera && device.isFrontFacing)
            {
                selectedCameraName = device.name;
                break;
            }
            else if (!useFrontCamera && !device.isFrontFacing)
            {
                selectedCameraName = device.name;
                break;
            }
        }

        // Fallback to first available camera
        if (string.IsNullOrEmpty(selectedCameraName))
        {
            selectedCameraName = devices[0].name;
            Debug.LogWarning($"GetCameraFeed: Preferred camera type not found, using: {selectedCameraName}");
        }

        // Create and configure the WebCamTexture
        webCamTexture = new WebCamTexture(selectedCameraName, requestedWidth, requestedHeight, requestedFPS);
        
        // Apply texture to the display RawImage
        if (displayImage != null)
        {
            displayImage.texture = webCamTexture;
            displayImage.material.mainTexture = webCamTexture;
        }
        else
        {
            Debug.LogWarning("GetCameraFeed: No RawImage assigned! Attempting to find one on this GameObject.");
            displayImage = GetComponent<RawImage>();
            if (displayImage != null)
            {
                displayImage.texture = webCamTexture;
            }
            else
            {
                Debug.LogError("GetCameraFeed: No RawImage component found! Please assign a RawImage to display the camera feed.");
                return;
            }
        }

        // Start the camera
        webCamTexture.Play();
        isCameraInitialized = true;
        Debug.Log($"GetCameraFeed: Camera started - {selectedCameraName} ({webCamTexture.width}x{webCamTexture.height})");
    }

    void Update()
    {
        // Adjust the RawImage aspect ratio to match the camera feed
        if (isCameraInitialized && webCamTexture.isPlaying && displayImage != null)
        {
            // Correct rotation for mobile devices
            int videoRotationAngle = webCamTexture.videoRotationAngle;
            displayImage.rectTransform.localEulerAngles = new Vector3(0, 0, -videoRotationAngle);
            
            // Handle mirroring for front camera
            float scaleY = webCamTexture.videoVerticallyMirrored ? -1f : 1f;
            displayImage.rectTransform.localScale = new Vector3(1f, scaleY, 1f);
        }
    }

    public WebCamTexture GetWebCamTexture()
    {
        return webCamTexture;
    }

    public bool IsCameraPlaying()
    {
        return webCamTexture != null && webCamTexture.isPlaying;
    }

    public void PauseCamera()
    {
        if (webCamTexture != null && webCamTexture.isPlaying)
        {
            webCamTexture.Pause();
            Debug.Log("GetCameraFeed: Camera paused");
        }
    }

    public void ResumeCamera()
    {
        if (webCamTexture != null && !webCamTexture.isPlaying)
        {
            webCamTexture.Play();
            Debug.Log("GetCameraFeed: Camera resumed");
        }
    }

    void OnDisable()
    {
        if (webCamTexture != null && webCamTexture.isPlaying)
        {
            webCamTexture.Stop();
        }
    }

    void OnDestroy()
    {
        if (webCamTexture != null)
        {
            webCamTexture.Stop();
            Destroy(webCamTexture);
        }
    }
}
