using CameraTests.UI;
using DrawnUi;
using DrawnUi.Camera;
using System.Diagnostics;
using AppoMobi.Specials;
using TestFaces.Services;

namespace TestFaces;

public partial class MainPage : ContentPage
{
    private AppCamera.PreviewDetectionMetrics? _lastPreviewMetrics;

    #region XAML HotReload

    // We need this to handle XAML hot reload propely.
    // It would just re-create a new instance of AppCanvas without disconnecting handler on the old one.
    // So we need some hacks to assure be behave like a singleton.

 
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (Handler == null)
        {
            AppCanvas.WasReloaded -= XamlHotReloadDetected;
            MainCanvas?.DisconnectHandlers();
            MainCanvas?.Dispose();
        }
        else
        {
            AppCanvas.WasReloaded += XamlHotReloadDetected;
            InitUi();
        }
    }

    private void XamlHotReloadDetected(object? sender, EventArgs e)
    {
        InitUi();
    }

    #endregion

    void InitUi()
    {
        Tasks.StartDelayed(TimeSpan.FromMilliseconds(500), () =>
        {
            OnUiLoaded();
        });
    }

    private readonly IFaceLandmarkDetector _detector;

    public MainPage(IFaceLandmarkDetector detector)
    {
        _detector = detector;

        try
        {
            InitializeComponent();
        }
        catch (Exception e)
        {
            Super.DisplayException(this, e);
        }
    }

    private void OnHotReload(object? sender, EventArgs eventArgs)
    {
         Debug.WriteLine("HOTRELOAD !!!");

         AttachHardware(false);
    }

    public void OnUiLoaded()
    {
        CameraControl.Detector = _detector;
        _detector.MaxFaces = CameraControl.MaxNumFaces;
        SyncConfidenceInputsFromCamera();
        AttachHardware(true);
        OnModeChanged(null, EventArgs.Empty);
    }

    // Fallback for Shell DataTemplate resolution (bypasses DI)
    public MainPage()
        : this(ResolveDependency<IFaceLandmarkDetector>())
    {
    }

    private static T ResolveDependency<T>() where T : notnull
    {
        return Application.Current!.Handler!.MauiContext!.Services.GetRequiredService<T>();
    }

    void ShowAlert(string title, string message)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync(title, message, "OK");
        });
    }


    #region DETECT FACE LANDMARKS

    private void SyncConfidenceInputsFromCamera()
    {
        // Keep the manual input fields in sync with the camera's current detector thresholds.
        DetectionConfidenceEntry.Text = CameraControl.MinFaceDetectionConfidence.ToString("0.00");
        PresenceConfidenceEntry.Text = CameraControl.MinFacePresenceConfidence.ToString("0.00");
        TrackingConfidenceEntry.Text = CameraControl.MinTrackingConfidence.ToString("0.00");
    }

    private void OnApplyConfidenceClicked(object? sender, EventArgs e)
    {
        try
        {
            // These inputs intentionally accept raw text so thresholds can be tuned manually while the sample is running.
            var detectionConfidence = ParseConfidenceOrThrow(DetectionConfidenceEntry.Text, nameof(DetectionConfidenceEntry));
            var presenceConfidence = ParseConfidenceOrThrow(PresenceConfidenceEntry.Text, nameof(PresenceConfidenceEntry));
            var trackingConfidence = ParseConfidenceOrThrow(TrackingConfidenceEntry.Text, nameof(TrackingConfidenceEntry));

            CameraControl.MinFaceDetectionConfidence = detectionConfidence;
            CameraControl.MinFacePresenceConfidence = presenceConfidence;
            CameraControl.MinTrackingConfidence = trackingConfidence;

            // Re-apply settings immediately so the detector instance can rebuild with the new thresholds if needed.
            CameraControl.Detector = _detector;
            SyncConfidenceInputsFromCamera();
            StatusLabel.Text = $"Confidence updated det {detectionConfidence:0.00}, pres {presenceConfidence:0.00}, track {trackingConfidence:0.00}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private static float ParseConfidenceOrThrow(string? rawValue, string inputName)
    {
        if (!float.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
            !float.TryParse(rawValue, out value))
        {
            throw new InvalidOperationException($"{inputName} must be a number between 0 and 1.");
        }

        if (value < 0f || value > 1f)
        {
            throw new InvalidOperationException($"{inputName} must be in the range 0..1.");
        }

        return value;
    }

    private async void OnModeChanged(object? sender, EventArgs e)
    {
        if (ModePicker != null)
        {
            var drawMode = ModePicker.SelectedIndex switch
            {
                1 => DetectionType.Rectangle,
                2 => DetectionType.Mask, // Spider-Man
                3 => DetectionType.Mask, // Cake Hat
                _ => DetectionType.Landmark
            };

            CameraControl.DrawMode = drawMode;

            MaskConfiguration? config = null;

            if (drawMode == DetectionType.Mask)
            {
                try
                {
                    config = ModePicker.SelectedIndex switch
                    {
                        3 => new MaskConfiguration
                        {
                            Filename = "hat_cake.png",
                            Position = MaskPosition.Top,
                            WidthMultiplier = 1.6f,
                            YOffsetRatio = 0.05f
                        },
                        _ => new MaskConfiguration
                        {
                            Filename = "mask_spiderman.png",
                            Position = MaskPosition.Inside,
                            WidthMultiplier = 1.25f,
                            YOffsetRatio = -0.2f
                        }
                    };

                    await CameraControl.SetMaskConfigurationAsync(config);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load mask image: {ex}");
                }
            }
            else
            {
                await CameraControl.SetMaskConfigurationAsync(null);
            }
        }
    }

    private async void OnPickPhotoClicked(object? sender, EventArgs e)
    {
        try
        {
            var results = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
            {
                Title = "Pick a Photo",
            });
            var result = results?.FirstOrDefault();
            if (result is null)
                return;

            // Open two separate streams: one for display, one for detection
            var displayStream = await result.OpenReadAsync();
            SelectedImage.Source = ImageSource.FromStream(() => displayStream);
            SelectedImage.IsVisible = true;

            // Show spinner
            StatusLabel.Text = "Detecting landmarks...";
            //Spinner.IsRunning = true;
            //Spinner.IsVisible = true;
            PickPhotoBtn.IsEnabled = false;

            _detector.MaxFaces = CameraControl.MaxNumFaces;

            FaceLandmarkResult detection;
            using (var detectionStream = await result.OpenReadAsync())
            {
                detection = await _detector.DetectAsync(detectionStream);
            }

            var faceCount = detection.Faces.Count;
            StatusLabel.Text = faceCount switch
            {
                0 => "No faces detected.",
                1 => "1 face detected.",
                _ => $"{faceCount} faces detected.",
            };
        }
        catch (PlatformNotSupportedException ex)
        {
            StatusLabel.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            //Spinner.IsRunning = false;
            //Spinner.IsVisible = false;
            PickPhotoBtn.IsEnabled = true;
        }
    }

    #endregion

    #region CAMERA

    public void AttachHardware(bool subscribe)
    {
        if (subscribe)
        {
            AttachHardware(false);

            CameraControl.PermissionsResult += OnPermissionsResultChanged;
            CameraControl.StateChanged += CameraControlOnStateChanged;
            CameraControl.OnError += OnCameraError;
            CameraControl.PreviewDetectionMeasured += OnPreviewDetectionMeasured;
            CameraControl.PreviewDetectionUpdated += OnPreviewDetectionUpdated;
            CameraControl.PreviewDetectionFailed += OnPreviewDetectionFailed;

            CameraControl.IsOn = true;

            Debug.WriteLine($"Camera attached {CameraControl.Uid}");
        }
        else
        {
            if (CameraControl != null)
            {
                CameraControl.PermissionsResult -= OnPermissionsResultChanged;
                CameraControl.StateChanged -= CameraControlOnStateChanged;
                CameraControl.OnError -= OnCameraError;
                CameraControl.PreviewDetectionMeasured -= OnPreviewDetectionMeasured;
                CameraControl.PreviewDetectionUpdated -= OnPreviewDetectionUpdated;
                CameraControl.PreviewDetectionFailed -= OnPreviewDetectionFailed;

                Debug.WriteLine($"Camera detached {CameraControl.Uid}");
            }
        }
    }

    private void OnCameraError(object? sender, string e)
    {
        ShowAlert("Camera Error", e);
    }

    private void OnPermissionsResultChanged(object? sender, bool e)
    {
        if (!e)
        {
            ShowAlert("Error", "The application does not have the required permissions to access all the camera features.");
        }
    }

    private void CameraControlOnStateChanged(object? sender, HardwareState e)
    {
        if (e == HardwareState.On)
        {
            StatusLabel.Text = "Camera ready";
        }
        else
        {
            StatusLabel.Text = "Camera stopped";
        }
    }

    private void OnPreviewDetectionUpdated(object? sender, FaceLandmarkResult detection)
    {
        var faceCount = detection.Faces.Count;
        var facesText = faceCount switch
        {
            0 => "No faces detected.",
            1 => "1 face.",
            _ => $"{faceCount} faces detected."
        };

        if (_lastPreviewMetrics == null)
        {
            StatusLabel.Text = facesText;
            return;
        }

        var metrics = _lastPreviewMetrics;
        var sourceText = metrics.ReusedCachedFrame ? "cached" : "live";
        var otherDetectorMilliseconds = Math.Max(
            0,
            metrics.DetectionMilliseconds
            - detection.ConversionMilliseconds
            - detection.InferenceMilliseconds
            - detection.ResultMappingMilliseconds);
        var backendText = detection.InferenceMilliseconds > 0
            ? $", conv {detection.ConversionMilliseconds:F1}, mp {detection.InferenceMilliseconds:F1}, map {detection.ResultMappingMilliseconds:F1}, other {otherDetectorMilliseconds:F1}, {(detection.UsedGpuDelegate ? "gpu" : "cpu")}"
            : string.Empty;

        StatusLabel.Text = $"{facesText} size {metrics.ResizeMilliseconds:F1}, det {metrics.DetectionMilliseconds:F1}{backendText}, {metrics.Width}x{metrics.Height}, {sourceText}";
    }

    private void OnPreviewDetectionMeasured(object? sender, AppCamera.PreviewDetectionMetrics metrics)
    {
        _lastPreviewMetrics = metrics;
    }

    private void OnPreviewDetectionFailed(object? sender, Exception ex)
    {
        Debug.WriteLine(ex);
        StatusLabel.Text = $"Detection error: {ex.Message}";
    }


    #endregion

}




