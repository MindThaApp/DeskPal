using DeskPal.Core.Pose;
using Microsoft.ML.OnnxRuntime;

namespace DeskPal.Pose;

/// <summary>
/// The on-device single-person pose model (MoveNet SinglePose Lightning, 192 x 192). Runs on the CPU with one
/// thread. Takes one BGRA image held in memory and returns the 17 body points; the image is only read, never kept.
/// Not thread-safe: one caller at a time (<see cref="PoseSession"/> makes sure of that).
/// </summary>
public sealed class PoseModel : IDisposable
{
    public const int InputSize = 192;
    public const string FileName = "movenet-singlepose-lightning.onnx";

    private static readonly long[] InputShape = [1, InputSize, InputSize, 3];
    private static readonly long[] OutputShape = [1, 1, PoseFrame.PointCount, 3];

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly int[] _input = new int[InputSize * InputSize * 3];
    private readonly float[] _output = new float[PoseFrame.PointCount * 3];
    private readonly OrtValue _inputValue;
    private readonly OrtValue _outputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;
    private bool _disposed;

    /// <summary>The bundled model file next to the app.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", FileName);

    private PoseModel(InferenceSession session)
    {
        _session = session;
        _inputNames = [session.InputMetadata.Keys.Single()];
        _outputNames = [session.OutputMetadata.Keys.Single()];
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, InputShape);
        _outputValue = OrtValue.CreateTensorValueFromMemory(_output, OutputShape);
        _inputs = [_inputValue];
        _outputs = [_outputValue];
    }

    /// <summary>Loads the model. Throws when the file is missing or the runtime cannot load it.</summary>
    public static PoseModel Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) throw new FileNotFoundException("Pose model not found.", path);
        // The runtime's own usage events are off: nothing about the pose check leaves the PC.
        try { OrtEnv.Instance().DisableTelemetryEvents(); } catch { }
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            EnableMemoryPattern = true,
        };
        // No busy-waiting threads between frames.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        var session = new InferenceSession(path, options);
        try { return new PoseModel(session); }
        catch { session.Dispose(); throw; }
    }

    /// <summary>
    /// Finds the pose in one BGRA8 image (rows of <paramref name="stride"/> bytes). The image is letterboxed into the
    /// model's square input so the body keeps its shape; the points come back as 0..1 of the original image, not
    /// mirrored.
    /// </summary>
    public PoseFrame Run(ReadOnlySpan<byte> bgra, int width, int height, int stride, TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("Image size does not match the buffer.");

        var box = Letterbox.For(width, height, InputSize);
        Fill(bgra, width, height, stride, box);
        _session.Run(_run, _inputNames, _inputs, _outputNames, _outputs);
        return Decode(_output, box, time);
    }

    /// <summary>Resamples the image into the int32 RGB input (bilinear; padding stays black).</summary>
    private void Fill(ReadOnlySpan<byte> bgra, int width, int height, int stride, Letterbox box)
    {
        Array.Clear(_input);
        float scale = 1f / box.Scale;
        for (int y = 0; y < box.Height; y++)
        {
            float sy = Math.Clamp((y + 0.5f) * scale - 0.5f, 0, height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, height - 1);
            float fy = sy - y0;
            var row0 = bgra.Slice(y0 * stride, width * 4);
            var row1 = bgra.Slice(y1 * stride, width * 4);
            int o = ((box.Top + y) * InputSize + box.Left) * 3;
            for (int x = 0; x < box.Width; x++, o += 3)
            {
                float sx = Math.Clamp((x + 0.5f) * scale - 0.5f, 0, width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, width - 1);
                float fx = sx - x0;
                int a = x0 * 4, b = x1 * 4;
                for (int c = 0; c < 3; c++)
                {
                    // BGRA in, RGB out.
                    int src = 2 - c;
                    float top = row0[a + src] + (row0[b + src] - row0[a + src]) * fx;
                    float bottom = row1[a + src] + (row1[b + src] - row1[a + src]) * fx;
                    _input[o + c] = (int)(top + (bottom - top) * fy + 0.5f);
                }
            }
        }
    }

    internal static PoseFrame Decode(ReadOnlySpan<float> output, Letterbox box, TimeSpan time)
    {
        var points = new Keypoint[PoseFrame.PointCount];
        for (int i = 0; i < points.Length; i++)
        {
            // MoveNet gives y, x, score per point, as 0..1 of the square input.
            float y = (output[i * 3] * InputSize - box.Top) / box.Height;
            float x = (output[i * 3 + 1] * InputSize - box.Left) / box.Width;
            float score = output[i * 3 + 2];
            points[i] = new Keypoint(Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f), Math.Clamp(score, 0f, 1f));
        }
        return new PoseFrame(time, points);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inputValue.Dispose();
        _outputValue.Dispose();
        _run.Dispose();
        _session.Dispose();
        // The last frame's pixels and points do not outlive the model.
        Array.Clear(_input);
        Array.Clear(_output);
    }
}

/// <summary>Where a width x height image sits inside the square model input after scaling to fit.</summary>
internal readonly record struct Letterbox(int Left, int Top, int Width, int Height, float Scale)
{
    public static Letterbox For(int width, int height, int size)
    {
        float scale = (float)size / Math.Max(width, height);
        int w = Math.Clamp((int)MathF.Round(width * scale), 1, size);
        int h = Math.Clamp((int)MathF.Round(height * scale), 1, size);
        return new Letterbox((size - w) / 2, (size - h) / 2, w, h, scale);
    }
}
