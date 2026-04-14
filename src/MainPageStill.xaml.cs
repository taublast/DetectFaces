using System.Diagnostics;
using TestFaces.Services;

namespace TestFaces;

public partial class MainPageStill : ContentPage
{
	private readonly IFaceLandmarkDetector _detector;

	public MainPageStill(IFaceLandmarkDetector detector)
	{
		_detector = detector;
		InitializeComponent();
	}

	// Fallback for Shell DataTemplate resolution (bypasses DI)
	public MainPageStill()
		: this(ResolveDependency<IFaceLandmarkDetector>())
	{
	}

	private static T ResolveDependency<T>() where T : notnull
	{
		return Application.Current!.Handler!.MauiContext!.Services.GetRequiredService<T>();
	}

	private async void OnModeChanged(object? sender, EventArgs e)
        {
                if (LandmarkDraw != null && ModePicker != null)
                {
                        LandmarkDraw.DrawMode = ModePicker.SelectedIndex switch 
                        {
                            1 => DetectionType.Rectangle,
                            2 => DetectionType.Mask, // Spider-Man
                            3 => DetectionType.Mask, // Cake Hat
                            _ => DetectionType.Landmark
                        };

                        if (LandmarkDraw.DrawMode == DetectionType.Mask)
                        {
                            try {
                                var config = ModePicker.SelectedIndex switch
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

                                if (LandmarkDraw.ActiveMaskConfig?.Filename != config.Filename || LandmarkDraw.MaskImage == null)
                                {
                                    LandmarkDraw.ActiveMaskConfig = config;
                                    using var stream = await FileSystem.OpenAppPackageFileAsync(config.Filename);
                                    LandmarkDraw.MaskImage = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
                                }
                            } catch (Exception ex) {
                                Debug.WriteLine($"Failed to load mask image: {ex}");
                            }
                        }

                        if (LandmarkOverlay.IsVisible)
                        {
                                LandmarkOverlay.Invalidate();
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

			// Reset overlay
			LandmarkDraw.Update(null);
			LandmarkOverlay.IsVisible = false;

			// Show spinner
			StatusLabel.Text = "Detecting landmarks...";
			Spinner.IsRunning = true;
			Spinner.IsVisible = true;
			PickPhotoBtn.IsEnabled = false;

			FaceLandmarkResult detection;
			using (var detectionStream = await result.OpenReadAsync())
			{
				detection = await _detector.DetectAsync(detectionStream);
			}

			// Update overlay
			LandmarkDraw.Update(detection);
			LandmarkOverlay.IsVisible = true;
			LandmarkOverlay.Invalidate();

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
			Spinner.IsRunning = false;
			Spinner.IsVisible = false;
			PickPhotoBtn.IsEnabled = true;
		}
	}
}




