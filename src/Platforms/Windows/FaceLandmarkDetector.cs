using Mediapipe.Net.Core;
using Mediapipe.Net.Framework;
using Mediapipe.Net.Framework.Format;
using Mediapipe.Net.Framework.Packets;
using Mediapipe.Net.Framework.Protobuf;
using Mediapipe.Net.Framework.Port;
using Mediapipe.Net.Native;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using TestFaces.Services;
using Windows.Graphics.Imaging;

using MpImageFormat = Mediapipe.Net.Framework.Protobuf.ImageFormat;

namespace TestFaces.Platforms.Windows;

public class FaceLandmarkDetector : IFaceLandmarkDetector
{


    private static readonly SemaphoreSlim s_detectLock = new(1, 1);
    private readonly SemaphoreSlim _liveSessionLock = new(1, 1);
    private LiveGraphSession? _liveSession;
    private int _maxFaces = 2;
    private float _minFaceDetectionConfidence = 0.5f;
    private float _minFacePresenceConfidence = 0.5f;
    private float _minTrackingConfidence = 0.5f;

    public event EventHandler<PreviewDetectionCompletedEventArgs>? PreviewDetectionCompleted;

    public event EventHandler<PreviewDetectionFailedEventArgs>? PreviewDetectionFailed;

    public int MaxFaces
    {
        get => _maxFaces;
        set
        {
            var normalized = Math.Max(1, value);
            if (_maxFaces == normalized)
                return;

            _maxFaces = normalized;
            _liveSession = null;
        }
    }

    public float MinFaceDetectionConfidence
    {
        get => _minFaceDetectionConfidence;
        set => _minFaceDetectionConfidence = Math.Clamp(value, 0f, 1f);
    }

    public float MinFacePresenceConfidence
    {
        get => _minFacePresenceConfidence;
        set => _minFacePresenceConfidence = Math.Clamp(value, 0f, 1f);
    }

    public float MinTrackingConfidence
    {
        get => _minTrackingConfidence;
        set => _minTrackingConfidence = Math.Clamp(value, 0f, 1f);
    }

    public async Task<FaceLandmarkResult> DetectAsync(Stream imageStream)
    {
        await s_detectLock.WaitAsync();
        try
        {
            var (bgraBytes, width, height) = await DecodeToBgra8Async(imageStream);
            return await DetectInternalAsync(width, height, imageFrame => CopyBgraToSrgbImageFrame(imageFrame, bgraBytes, width, height));
        }
        finally
        {
            s_detectLock.Release();
        }
    }

    public void EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await DetectPreviewAsync(rgbaBytes, request.Width, request.Height);
                PreviewDetectionCompleted?.Invoke(this, new PreviewDetectionCompletedEventArgs(request, result));
            }
            catch (Exception ex)
            {
                PreviewDetectionFailed?.Invoke(this, new PreviewDetectionFailedEventArgs(request, ex));
            }
        });
    }

    private async Task<FaceLandmarkResult> DetectPreviewAsync(byte[] rgbaBytes, int width, int height)
    {
        if (rgbaBytes == null || width <= 0 || height <= 0 || rgbaBytes.Length < width * height * 4)
            return new FaceLandmarkResult();

        await s_detectLock.WaitAsync();
        try
        {
            var session = await GetOrCreateLiveSessionAsync();
            return await session.DetectAsync(width, height, imageFrame => CopyRgbaToSrgbImageFrame(imageFrame, rgbaBytes, width, height));
        }
        finally
        {
            s_detectLock.Release();
        }
    }

    private async Task<FaceLandmarkResult> DetectInternalAsync(int width, int height, Action<ImageFrame> copyPixels)
    {
        string modelCwd;
        try
        {
            await s_modelBundleCache.EnsureLoadedAsync();
            modelCwd = await s_modelBundleCache.EnsureExtractedToDiskAsync();
        }
        catch (Exception ex)
        {
            throw Wrap("loading model bundle", ex);
        }

        string graphConfig;
        try
        {
            graphConfig = await s_graphConfigCache.GetAsync();
        }
        catch (Exception ex)
        {
            throw Wrap("loading graph config", ex);
        }

        var faceLandmarkLists = new List<NormalizedLandmarkList>();
        using var cwdScope = new CurrentDirectoryScope(modelCwd);
        CalculatorGraph graph;
        try
        {
            graph = new CalculatorGraph(graphConfig);
        }
        catch (Exception ex)
        {
            throw Wrap("creating CalculatorGraph", ex);
        }

        using (graph)
        {
            OutputStreamPoller<NormalizedLandmarkList> poller;
            try
            {
                // Observe the internal single-face landmark stream directly.
                // MediaPipe allows observing any named stream in the graph.
                var statusOrPoller = graph.AddOutputStreamPoller<NormalizedLandmarkList>("face_landmarks");
                statusOrPoller.Status.AssertOk();
                poller = statusOrPoller.Value();
            }
            catch (Exception ex)
            {
                throw Wrap("AddOutputStreamPoller(face_landmarks)", ex);
            }

            using var numFacesPacket = new IntPacket(_maxFaces);
            using var usePrevLandmarksPacket = new BoolPacket(false);
            using var withAttentionPacket = new BoolPacket(false);

            using var sidePackets = new SidePacket();
            sidePackets.Emplace("num_faces", numFacesPacket);
            sidePackets.Emplace("use_prev_landmarks", usePrevLandmarksPacket);
            sidePackets.Emplace("with_attention", withAttentionPacket);

            try
            {
                graph.StartRun(sidePackets).AssertOk();
            }
            catch (Exception ex)
            {
                throw Wrap("StartRun", ex);
            }

            try
            {
                // We let MediaPipe allocate its native memory internally for the ImageFrame buffer.
                var imageFrame = new ImageFrame(MpImageFormat.Types.Format.Srgb, width, height);
                copyPixels(imageFrame);

var timestamp = new Timestamp(1L);
                
                // This typically takes ownership of the unmanaged pointer from imageFrame.
                // We do NOT use 'using' around imageFrame so we do not double free it.
                var packet = new ImageFramePacket(imageFrame, timestamp);

                try
                {
                    // Push to the running graph. Note: this runs asynchronously in the C++ layer.
                    graph.AddPacketToInputStream("image", packet).AssertOk();
                }
                finally
                {
                    // We must manually dispose the C# proxy wrappers cleanly to decrement their ref counts,
                    // otherwise the GC destroying them late (while Mediapipe worker thread is iterating the frame)
                    // causes a double-free Access Violation!
                    packet.Dispose();
                    timestamp.Dispose();
                }
            }
            catch (Exception ex)
            {
                throw Wrap("AddPacketToInputStream(image)", ex);
            }

            try
            {
                graph.CloseInputStream("image").AssertOk();
            }
            catch (Exception ex)
            {
                throw Wrap("CloseInputStream(image)", ex);
            }

            try
            {
                using var outputPacket = new NormalizedLandmarkListPacket();
                while (poller.Next(outputPacket))
                {
                    var landmarks = outputPacket.Get();
                    if (landmarks is not null)
                        faceLandmarkLists.Add(landmarks);
                }
            }
            catch (Exception ex)
            {
                throw Wrap("polling face_landmarks", ex);
            }

            try
            {
                graph.WaitUntilDone().AssertOk();
            }
            catch (Exception ex)
            {
                throw Wrap("WaitUntilDone", ex);
            }
            finally
            {
                poller.Dispose();
            }
        }

        List<DetectedFace> faces;
        lock (faceLandmarkLists)
        {
            faces = MapDetectedFaces(faceLandmarkLists);
        }

        return new FaceLandmarkResult
        {
            Faces = faces,
            ImageWidth = width,
            ImageHeight = height,
        };
    }

    private static InvalidOperationException Wrap(string step, Exception ex)
        => new InvalidOperationException($"MediaPipe (Windows): {step} failed. {ex.Message}", ex);

    private const string GraphFileName = "face_landmark_front_cpu.pbtxt";
    private const string TaskBundleFileName = "face_landmarker.task";

    private static readonly GraphConfigCache s_graphConfigCache = new(GraphFileName);
    private static readonly TaskModelBundleCache s_modelBundleCache = new(TaskBundleFileName);

    private sealed class CurrentDirectoryScope : IDisposable
    {
        private readonly string _previous;
        public CurrentDirectoryScope(string current)
        {
            _previous = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(current);
        }

        public void Dispose()
        {
            try
            {
                Directory.SetCurrentDirectory(_previous);
            }
            catch
            {
                // Best-effort restore.
            }
        }
    }

    private static async Task<(byte[] bgraBytes, int width, int height)> DecodeToBgra8Async(Stream imageStream)
    {
        await using var ms = new MemoryStream();
        await imageStream.CopyToAsync(ms);
        ms.Position = 0;

        using var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var width = (int)softwareBitmap.PixelWidth;
        var height = (int)softwareBitmap.PixelHeight;
        var bytes = new byte[width * height * 4];
        softwareBitmap.CopyToBuffer(bytes.AsBuffer());
        return (bytes, width, height);
    }

    private static void CopyBgraToSrgbImageFrame(ImageFrame imageFrame, byte[] bgraBytes, int width, int height)
    {
        if (bgraBytes.Length < checked(width * height * 4))
            throw new ArgumentException("Invalid BGRA buffer size", nameof(bgraBytes));

        var dstBasePtr = imageFrame.MutablePixelData();
        if (dstBasePtr == IntPtr.Zero)
            throw new InvalidOperationException("ImageFrame has no writable pixel buffer");

        var dstWidthStep = imageFrame.WidthStep();
        var srcRowBytes = checked(width * 4);
        var dstRowBytes = checked(width * 3);

        var rgbRow = new byte[dstRowBytes];

        for (int y = 0; y < height; y++)
        {
            var srcOffset = checked(y * srcRowBytes);
            for (int x = 0; x < width; x++)
            {
                byte b = bgraBytes[srcOffset + x * 4 + 0];
                byte g = bgraBytes[srcOffset + x * 4 + 1];
                byte r = bgraBytes[srcOffset + x * 4 + 2];
                // RGB
                rgbRow[x * 3 + 0] = r;
                rgbRow[x * 3 + 1] = g;
                rgbRow[x * 3 + 2] = b;
            }
            
            var dstRowPtr = IntPtr.Add(dstBasePtr, checked(y * dstWidthStep));
            Marshal.Copy(rgbRow, 0, dstRowPtr, dstRowBytes);
        }
    }

    private static void CopyRgbaToSrgbImageFrame(ImageFrame imageFrame, byte[] rgbaBytes, int width, int height)
    {
        if (rgbaBytes.Length < checked(width * height * 4))
            throw new ArgumentException("Invalid RGBA buffer size", nameof(rgbaBytes));

        var dstBasePtr = imageFrame.MutablePixelData();
        if (dstBasePtr == IntPtr.Zero)
            throw new InvalidOperationException("ImageFrame has no writable pixel buffer");

        var dstWidthStep = imageFrame.WidthStep();
        var srcRowBytes = checked(width * height > 0 ? width * 4 : 0);
        var dstRowBytes = checked(width * 3);

        var rgbRow = new byte[dstRowBytes];

        for (int y = 0; y < height; y++)
        {
            var srcOffset = checked(y * srcRowBytes);
            for (int x = 0; x < width; x++)
            {
                rgbRow[x * 3 + 0] = rgbaBytes[srcOffset + x * 4 + 0];
                rgbRow[x * 3 + 1] = rgbaBytes[srcOffset + x * 4 + 1];
                rgbRow[x * 3 + 2] = rgbaBytes[srcOffset + x * 4 + 2];
            }

            var dstRowPtr = IntPtr.Add(dstBasePtr, y * dstWidthStep);
            Marshal.Copy(rgbRow, 0, dstRowPtr, dstRowBytes);
        }
    }

    private sealed class GraphConfigCache
    {
        private readonly string _assetName;
        private string? _cached;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public GraphConfigCache(string assetName)
        {
            _assetName = assetName;
        }

        public async Task<string> GetAsync()
        {
            if (_cached is not null)
                return _cached;

            await _lock.WaitAsync();
            try
            {
                if (_cached is not null)
                    return _cached;

                await using var stream = await FileSystem.OpenAppPackageFileAsync(_assetName);
                using var reader = new StreamReader(stream);
                _cached = await reader.ReadToEndAsync();
                return _cached;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private sealed class TaskModelBundleCache
    {
        private readonly string _assetName;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SemaphoreSlim _diskLock = new(1, 1);
        private Dictionary<string, byte[]>? _resources;
        private string? _extractedRoot;

        public TaskModelBundleCache(string assetName)
        {
            _assetName = assetName;
        }

        public bool IsLoaded => _resources is not null;

        public async Task EnsureLoadedAsync()
        {
            if (IsLoaded)
                return;

            await _lock.WaitAsync();
            try
            {
                if (IsLoaded)
                    return;

                var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                async Task LoadModel(string fileName, string logicalPath)
                {
                    await using var stream = await FileSystem.OpenAppPackageFileAsync(fileName);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    resources[logicalPath] = ms.ToArray();
                }

                await LoadModel("face_detection_short_range.tflite", "mediapipe/modules/face_detection/face_detection_short_range.tflite");
                await LoadModel("face_landmark.tflite", "mediapipe/modules/face_landmark/face_landmark.tflite");

                _resources = resources;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<string> EnsureExtractedToDiskAsync()
        {
            await EnsureLoadedAsync();

            if (_extractedRoot is not null)
                return _extractedRoot;

            await _diskLock.WaitAsync();
            try
            {
                if (_extractedRoot is not null)
                    return _extractedRoot;

                if (_resources is null)
                    throw new InvalidOperationException("Model bundle not loaded yet.");

                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mediapipe-task", "face_landmarker");

                // Extract all .tflite files (and the canonical module paths we inserted) so native
                // code can load them using regular file I/O without managed callbacks.
                foreach (var (key, bytes) in _resources)
                {
                    if (!key.EndsWith(".tflite", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var relative = key.Replace('\\', '/');
                    var outPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    if (File.Exists(outPath))
                    {
                        var existingLength = new FileInfo(outPath).Length;
                        if (existingLength == bytes.Length)
                            continue;
                    }

                    await File.WriteAllBytesAsync(outPath, bytes);
                }

                _extractedRoot = root;
                return root;
            }
            finally
            {
                _diskLock.Release();
            }
        }

        public byte[] GetResourceBytes(string path)
        {
            if (_resources is null)
                throw new InvalidOperationException("Model bundle not loaded yet.");

            if (_resources.TryGetValue(path, out var bytes))
                return bytes;

            // Some callers may request just the filename.
            var fileName = Path.GetFileName(path).Replace('\\', '/');
            var match = _resources.FirstOrDefault(kvp => kvp.Key.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
                return match.Value;

            throw new FileNotFoundException($"MediaPipe requested resource '{path}', but it was not found in the bundled task file.");
        }
    }

    private async Task<LiveGraphSession> GetOrCreateLiveSessionAsync()
    {
        if (_liveSession != null)
            return _liveSession;

        await _liveSessionLock.WaitAsync();
        try
        {
            if (_liveSession != null)
                return _liveSession;

            await s_modelBundleCache.EnsureLoadedAsync();
            var modelCwd = await s_modelBundleCache.EnsureExtractedToDiskAsync();
            var graphConfig = await s_graphConfigCache.GetAsync();

            _liveSession = new LiveGraphSession(graphConfig, modelCwd, _maxFaces);
            return _liveSession;
        }
        finally
        {
            _liveSessionLock.Release();
        }
    }

    private sealed class LiveGraphSession : IDisposable
    {
        private readonly CurrentDirectoryScope _cwdScope;
        private readonly CalculatorGraph _graph;
        private readonly OutputStreamPoller<NormalizedLandmarkList> _landmarksPoller;
        private readonly int _maxFaces;
        private long _timestampCounter;

        public LiveGraphSession(string graphConfig, string modelCwd, int maxFaces)
        {
            _maxFaces = maxFaces;
            _cwdScope = new CurrentDirectoryScope(modelCwd);
            _graph = new CalculatorGraph(graphConfig);

            using var numFacesPacket = new IntPacket(_maxFaces);
            using var usePrevLandmarksPacket = new BoolPacket(true);
            using var withAttentionPacket = new BoolPacket(false);
            using var sidePackets = new SidePacket();
            sidePackets.Emplace("num_faces", numFacesPacket);
            sidePackets.Emplace("use_prev_landmarks", usePrevLandmarksPacket);
            sidePackets.Emplace("with_attention", withAttentionPacket);

            var landmarksPollerStatus = _graph.AddOutputStreamPoller<NormalizedLandmarkList>("face_landmarks");
            landmarksPollerStatus.Status.AssertOk();
            _landmarksPoller = landmarksPollerStatus.Value();
            _landmarksPoller.SetMaxQueueSize(_maxFaces);

            _graph.SetInputStreamMaxQueueSize("image", 1).AssertOk();
            _graph.StartRun(sidePackets).AssertOk();
        }

        public Task<FaceLandmarkResult> DetectAsync(int width, int height, Action<ImageFrame> copyPixels)
        {
            long timestamp = Interlocked.Increment(ref _timestampCounter);

            var imageFrame = new ImageFrame(MpImageFormat.Types.Format.Srgb, width, height);
            copyPixels(imageFrame);

            var ts = new Timestamp(timestamp);
            var packet = new ImageFramePacket(imageFrame, ts);
            try
            {
                _graph.AddPacketToInputStream("image", packet).AssertOk();
            }
            finally
            {
                packet.Dispose();
                ts.Dispose();
            }

            _graph.WaitUntilIdle().AssertOk();

            var faces = new List<DetectedFace>(Math.Max(0, _landmarksPoller.QueueSize()));
            using var landmarkPacket = new NormalizedLandmarkListPacket();
            while (_landmarksPoller.QueueSize() > 0 && _landmarksPoller.Next(landmarkPacket))
            {
                var landmarks = landmarkPacket.Get();
                if (landmarks is null)
                    continue;

                faces.Add(MapDetectedFace(landmarks));
            }

            return Task.FromResult(new FaceLandmarkResult
            {
                Faces = faces,
                ImageWidth = width,
                ImageHeight = height,
            });
        }

        public void Dispose()
        {
            _graph.CloseAllPacketSources().AssertOk();
            _graph.WaitUntilDone().AssertOk();
            _landmarksPoller.Dispose();
            _graph.Dispose();
            _cwdScope.Dispose();
        }
    }

    private static List<DetectedFace> MapDetectedFaces(List<NormalizedLandmarkList> faceLandmarkLists)
    {
        var faces = new List<DetectedFace>(faceLandmarkLists.Count);
        for (int faceIndex = 0; faceIndex < faceLandmarkLists.Count; faceIndex++)
        {
            faces.Add(MapDetectedFace(faceLandmarkLists[faceIndex]));
        }

        return faces;
    }

    private static DetectedFace MapDetectedFace(NormalizedLandmarkList landmarks)
    {
        var sourceLandmarks = landmarks.Landmark;
        var points = new List<NormalizedPoint>(sourceLandmarks.Count);
        for (int landmarkIndex = 0; landmarkIndex < sourceLandmarks.Count; landmarkIndex++)
        {
            var landmark = sourceLandmarks[landmarkIndex];
            points.Add(new NormalizedPoint(landmark.X, landmark.Y));
        }

        return new DetectedFace
        {
            Landmarks = points,
        };
    }
}
