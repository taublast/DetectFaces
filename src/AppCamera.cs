using DetectFaces.Services;
using DrawnUi.Camera;
using SkiaSharp;
using System.Diagnostics;


namespace CameraTests.UI
{
    public partial class AppCamera : SkiaCamera
    {
        /// <summary>
        /// Initializes the sample camera with audio, preview processing, and real-time frame processing
        /// enabled so face detection can run directly against incoming preview frames.
        /// </summary>
        public AppCamera()
        {
            //set defaults for this camera, we set base to be able to do video recording with sound
            NeedPermissionsSet = NeedPermissions.Camera | NeedPermissions.Gallery;
            CaptureMode = CaptureModeType.Still;

            //audio 
            this.EnableAudioMonitoring = false;
            Facing = CameraPosition.Selfie;

            ProcessFrame = OnFrameProcessing;
            ProcessPreview = OnFrameProcessing;
            this.UseRealtimeVideoProcessing = true;

#if DEBUG
            VideoDiagnosticsOn = true;
#endif
        }

        #region Lifecycle

        /// <summary>
        /// Releases detector subscriptions and stops the preview-detection pipeline before base disposal.
        /// </summary>
        public override void OnDisposing()
        {
            Detector = null;
            StopDetectionWorker();
            base.OnDisposing();
        }

        /// <summary>
        /// Disposes paints, bitmaps, and filter state owned by this sample camera before the control tree
        /// is torn down.
        /// </summary>
        public override void OnWillDisposeWithChildren()
        {
            base.OnWillDisposeWithChildren();

            _paintRec?.Dispose();
            _paintRec = null;
            _paintPreview?.Dispose();
            _paintPreview = null;
            _paintDetectionFrameStroke?.Dispose();
            _paintDetectionFrameStroke = null;
            _paintDetectionDotsStroke?.Dispose();
            _paintDetectionDotsStroke = null;
            _maskPaint?.Dispose();
            _maskPaint = null;
            MaskBitmap?.Dispose();
            MaskBitmap = null;
            MaskImage?.Dispose();
            MaskImage = null;
            _filtersX = null;
            _filtersY = null;
        }

        #endregion

        #region Audio

        /// <summary>
        /// Backing bindable property for <see cref="UseGain"/>.
        /// </summary>
        public static readonly BindableProperty UseGainProperty = BindableProperty.Create(
            nameof(UseGain),
            typeof(bool),
            typeof(AppCamera),
            false);

        /// <summary>
        /// Gets or sets whether microphone PCM samples should be amplified before they continue through
        /// the sample pipeline.
        /// </summary>
        public bool UseGain
        {
            get => (bool)GetValue(UseGainProperty);
            set => SetValue(UseGainProperty, value);
        }

        /// <summary>
        /// Gain multiplier applied to raw PCM when UseGain is true.
        /// </summary>
        public float GainFactor { get; set; } = 3.0f;


        /// <summary>
        /// Raised whenever a captured audio sample becomes available to the sample app.
        /// </summary>
        public event Action<AudioSample>? OnAudioSample;

        /// <summary>
        /// Applies optional gain to captured PCM audio, forwards the sample to listeners, and then lets
        /// the base camera pipeline continue processing the same sample.
        /// </summary>
        /// <param name="sample">The audio sample received from the capture pipeline.</param>
        /// <returns>The sample that should continue through the base pipeline.</returns>
        protected override AudioSample OnAudioSampleAvailable(AudioSample sample)
        {
            if (UseGain && sample.Data != null && sample.Data.Length > 1)
            {
                AmplifyPcm16(sample.Data, GainFactor);
            }

            OnAudioSample?.Invoke(sample);

            return base.OnAudioSampleAvailable(sample);
        }

        /// <summary>
        /// Amplifies PCM16 audio data in-place. Zero allocations.
        /// </summary>
        private static void AmplifyPcm16(byte[] data, float gain)
        {
            for (int i = 0; i < data.Length - 1; i += 2)
            {
                int sample = (short)(data[i] | (data[i + 1] << 8));
                sample = (int)(sample * gain);

                // Clamp to 16-bit range
                if (sample > 32767) sample = 32767;
                else if (sample < -32768) sample = -32768;

                data[i] = (byte)(sample & 0xFF);
                data[i + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        #endregion

        #region Hardware State

        void RefreshGpsLocationIfNeeded()
        {
            if (InjectGpsLocation)
            {
                MainThread.BeginInvokeOnMainThread(() => { _ = RefreshGpsLocation(); });
            }
        }

        public override void OnStateChanged(HardwareState state)
        {
            base.OnStateChanged(state);

            if (state == HardwareState.On)
            {
                if (Display != null)
                {
                    Display.Blur = 0;
                }

                RefreshGpsLocationIfNeeded();
            }
            else
            {
                if (Display != null)
                {
                    Display.Blur = 10;
                }
            }
        }

        #endregion
    }
}