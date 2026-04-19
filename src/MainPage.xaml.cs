using CameraTests.UI;
using DrawnUi;
using DrawnUi.Camera;
using DrawnUi.Draw;
using System.Diagnostics;
using AppoMobi.Specials;
using DetectFaces.Services;

namespace DetectFaces;

public partial class MainPage : ContentPage
{
    private AppCamera.PreviewDetectionMetrics? _lastPreviewMetrics;
    private bool _isCapturingPhoto;
    private bool _uiLoaded;
    private bool _hardwareAttached;

    #region XAML HotReload

    // We need this to handle XAML hot reload propely.
    // It would just re-create a new instance of AppCanvas without disconnecting handler on the old one.
    // So we need some hacks to assure be behave like a singleton.

 
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (Handler == null)
        {
            AttachHardware(false);
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
            ModePicker.SelectedIndex = DetectionSettings.InitialDetectionType switch
            {
                DetectionType.Rectangle => 1,
                DetectionType.Mask => 2,
                _ => 0 // Landmark
            };
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
        _uiLoaded = true;
        EnsureHardwareAttached();
        OnModeChanged(null, EventArgs.Empty);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (_uiLoaded)
        {
            EnsureHardwareAttached();
        }
    }

    protected override void OnDisappearing()
    {
        AttachHardware(false);
        ModePicker?.Unfocus();
        Unfocus();

        base.OnDisappearing();
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

                    await CameraControl.SetupMaskAsync(config);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load mask image: {ex}");
                }
            }
            else
            {
                await CameraControl.SetupMaskAsync(null);
            }
        }
    }

    private void OnDebugClicked(object? sender, EventArgs e)
    {
#if ANDROID
        DetectFaces.Platforms.Droid.FaceLandmarkDetector.UseFastApi =
            !DetectFaces.Platforms.Droid.FaceLandmarkDetector.UseFastApi;

        var state = DetectFaces.Platforms.Droid.FaceLandmarkDetector.UseFastApi ? "YES" : "NO";
        DebugBtn.Text = $"Use Fast API: {state}";
#endif
    }

    #endregion

    #region CAMERA

    private void EnsureHardwareAttached()
    {
        if (_hardwareAttached)
            return;

        CameraControl.Detector = _detector;
        _detector.MaxFaces = CameraControl.MaxNumFaces;
        AttachHardware(true);
    }

    public void AttachHardware(bool subscribe)
    {
        if (subscribe)
        {
            if (_hardwareAttached)
                return;

            AttachHardware(false);
            CameraControl.Detector = _detector;

            CameraControl.PermissionsResult += OnPermissionsResultChanged;
            CameraControl.StateChanged += CameraControlOnStateChanged;
            CameraControl.OnError += OnCameraError;
            CameraControl.PropertyChanged += OnCameraControlPropertyChanged;
            CameraControl.CaptureSuccess += OnCaptureSuccess;
            CameraControl.CaptureFailed += OnCaptureFailed;
            CameraControl.PreviewDetectionMeasured += OnPreviewDetectionMeasured;
            CameraControl.PreviewDetectionUpdated += OnPreviewDetectionUpdated;
            CameraControl.PreviewDetectionFailed += OnPreviewDetectionFailed;

            UpdateCaptureButtonVisualState();

            _hardwareAttached = true;

            Debug.WriteLine($"Camera attached {CameraControl.Uid}");
        }
        else
        {
            if (CameraControl != null)
            {
                _hardwareAttached = false;
                CameraControl.IsOn = false;
                CameraControl.Detector = null;
                CameraControl.PermissionsResult -= OnPermissionsResultChanged;
                CameraControl.StateChanged -= CameraControlOnStateChanged;
                CameraControl.OnError -= OnCameraError;
                CameraControl.PropertyChanged -= OnCameraControlPropertyChanged;
                CameraControl.CaptureSuccess -= OnCaptureSuccess;
                CameraControl.CaptureFailed -= OnCaptureFailed;
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

    private async void OnCapturePhotoTapped(object? sender, ControlTappedEventArgs e)
    {
        if (_isCapturingPhoto)
            return;

        if (CameraControl.State != HardwareState.On || CameraControl.IsBusy)
            return;

        try
        {
            _isCapturingPhoto = true;

            if (CapturePhotoButton != null)
            {
                CapturePhotoButton.IsEnabled = false;
            }

            UpdateCaptureButtonVisualState();

            await CameraControl.TakePicture();
        }
        catch (Exception ex)
        {
            _isCapturingPhoto = false;
            ResetCaptureButton();
            ShowAlert("Capture Failed", ex.Message);
        }
    }

    private void OnCaptureSuccess(object? sender, CapturedImage captured)
    {
        Tasks.StartDelayed(TimeSpan.FromMilliseconds(16), async () =>
        {
            try
            {
                var savedPath = await CameraControl.SaveToGalleryAsync(captured, "DetectFaces");
                if (string.IsNullOrEmpty(savedPath))
                {
                    ShowAlert("Save Failed", "Photo was captured but could not be saved to gallery.");
                }
            }
            catch (Exception ex)
            {
                ShowAlert("Save Failed", ex.Message);
            }
            finally
            {
                _isCapturingPhoto = false;
                MainThread.BeginInvokeOnMainThread(ResetCaptureButton);
            }
        });
    }

    private void OnCaptureFailed(object? sender, Exception ex)
    {
        _isCapturingPhoto = false;
        ResetCaptureButton();
        ShowAlert("Capture Failed", ex.Message);
    }

    private void OnCameraControlPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraControl.IsBusy) || string.IsNullOrEmpty(e.PropertyName))
        {
            MainThread.BeginInvokeOnMainThread(UpdateCaptureButtonVisualState);
        }
    }

    private void ResetCaptureButton()
    {
        if (CapturePhotoButton == null)
            return;

        UpdateCaptureButtonVisualState();
    }

    private void UpdateCaptureButtonVisualState()
    {
        if (CapturePhotoButton == null || CapturePhotoButtonInner == null)
            return;

        var isBusy = CameraControl?.IsBusy == true || _isCapturingPhoto;
        var isReady = CameraControl?.State == HardwareState.On && !isBusy;

        CapturePhotoButton.IsEnabled = isReady;
        CapturePhotoButton.Opacity = CameraControl?.State == HardwareState.On ? 1.0 : 0.7;
        CapturePhotoButtonInner.Opacity = isBusy ? 0.5 : 1;
        CapturePhotoButton.ScaleX = isBusy ? 0.92 : 1.0;
        CapturePhotoButton.ScaleY = isBusy ? 0.92 : 1.0;
    }

    private void CameraControlOnStateChanged(object? sender, HardwareState e)
    {
        UpdateCaptureButtonVisualState();

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

        //SHORT
        //StatusLabel.Text = $"{facesText}  benchmark {metrics.DetectionMilliseconds:F1}";
        //return;

        //FULL

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

    private void MainCanvas_OnWillFirstTimeDraw(object? sender, SkiaDrawingContext? e)
    {
        Tasks.StartDelayed(TimeSpan.FromMilliseconds(500), () =>
        {
            CameraControl.IsOn = true;
        });
    }

    private void Button_OnClicked(object? sender, EventArgs e)
    {
        CameraControl.IsOn = !CameraControl.IsOn;
    }
}




