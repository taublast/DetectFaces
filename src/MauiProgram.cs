global using DrawnUi.Draw;

using Microsoft.Extensions.Logging;
using TestFaces.Services;

namespace TestFaces;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.UseDrawnUi(new()
            {
                UseDesktopKeyboard = true,

                //portrait
                DesktopWindow = new()
                {
                    Height = 800,
                    Width = 375,
                }

                //landscape
                //DesktopWindow = new()
                //{
                //    Height = 500,
                //    Width = 750,
                //}
            });

        builder.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Face landmark detection — platform-specific implementations
#if ANDROID
		builder.Services.AddSingleton<IFaceLandmarkDetector, Platforms.Droid.FaceLandmarkDetector>();
#elif IOS
		builder.Services.AddSingleton<IFaceLandmarkDetector, Platforms.iOS.FaceLandmarkDetector>();
#elif MACCATALYST
		builder.Services.AddSingleton<IFaceLandmarkDetector, Platforms.MacCatalyst.FaceLandmarkDetector>();
#elif WINDOWS
		builder.Services.AddSingleton<IFaceLandmarkDetector, Platforms.Windows.FaceLandmarkDetector>();
#endif

		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

        //Super.MaxFps = 30;

		return builder.Build();
	}
}
