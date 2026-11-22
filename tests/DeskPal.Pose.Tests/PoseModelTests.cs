using System.Diagnostics;
using DeskPal.Core.Pose;
using DeskPal.Pose;
using Windows.Graphics.Imaging;
using Xunit.Abstractions;

namespace DeskPal.Pose.Tests;

public sealed class PoseModelTests(ITestOutputHelper output)
{
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", PoseModel.FileName);
    private static string ImagePath => Path.Combine(AppContext.BaseDirectory, "TestData", "standing-person.jpg");

    private static async Task<PoseImage> LoadImageAsync()
    {
        using var stream = File.OpenRead(ImagePath).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var image = new PoseImage();
        image.CopyFrom(bitmap);
        return image;
    }

    [Fact]
    public async Task Finds_the_body_of_a_standing_person()
    {
        var image = await LoadImageAsync();
        using var model = PoseModel.Load(ModelPath);

        var pose = model.Run(image.Pixels, image.Width, image.Height, image.Stride, TimeSpan.FromSeconds(1));

        Assert.Equal(PoseFrame.PointCount, pose.Points.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), pose.Time);
        foreach (var p in Enum.GetValues<BodyPoint>())
            output.WriteLine($"{p}: x={pose[p].X:F3} y={pose[p].Y:F3} score={pose[p].Score:F2}");

        foreach (var p in new[] { BodyPoint.Nose, BodyPoint.LeftShoulder, BodyPoint.RightShoulder, BodyPoint.LeftHip, BodyPoint.RightHip })
            Assert.True(pose[p].Score > 0.3f, $"{p} score {pose[p].Score}");

        // Head above shoulders above hips, in image coordinates (y grows downwards).
        float shoulderY = (pose[BodyPoint.LeftShoulder].Y + pose[BodyPoint.RightShoulder].Y) / 2;
        float hipY = (pose[BodyPoint.LeftHip].Y + pose[BodyPoint.RightHip].Y) / 2;
        Assert.True(pose[BodyPoint.Nose].Y < shoulderY && shoulderY < hipY);
        // Facing the camera and not mirrored: the person's left shoulder is on the right of the image.
        Assert.True(pose[BodyPoint.LeftShoulder].X > pose[BodyPoint.RightShoulder].X);
        // The person stands in the middle of the photo.
        Assert.InRange(pose[BodyPoint.Nose].X, 0.35f, 0.65f);
        Assert.InRange(pose[BodyPoint.Nose].Y, 0.1f, 0.4f);
    }

    [Fact]
    public async Task Inference_is_light_on_one_cpu_thread()
    {
        var image = await LoadImageAsync();
        using var model = PoseModel.Load(ModelPath);
        for (int i = 0; i < 5; i++) model.Run(image.Pixels, image.Width, image.Height, image.Stride, TimeSpan.Zero);

        const int runs = 50;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++) model.Run(image.Pixels, image.Width, image.Height, image.Stride, TimeSpan.Zero);
        double ms = sw.Elapsed.TotalMilliseconds / runs;
        output.WriteLine($"mean {ms:F2} ms per frame (resample + model)");
        Assert.True(ms < 40, $"{ms:F1} ms per frame");
    }

    [Fact]
    public void Blank_frame_runs_without_a_confident_body()
    {
        using var model = PoseModel.Load(ModelPath);
        var black = new byte[320 * 240 * 4];

        var pose = model.Run(black, 320, 240, 320 * 4, TimeSpan.Zero);

        Assert.Equal(PoseFrame.PointCount, pose.Points.Count);
        Assert.All(pose.Points, p => Assert.InRange(p.X, 0f, 1f));
        Assert.True(pose.Points.Average(p => p.Score) < 0.3f);
    }

    [Fact]
    public void Rejects_a_buffer_that_does_not_match_the_size()
    {
        using var model = PoseModel.Load(ModelPath);
        Assert.Throws<ArgumentException>(() => model.Run(new byte[100], 320, 240, 320 * 4, TimeSpan.Zero));
    }

    [Fact]
    public void Missing_model_file_throws()
    {
        Assert.Throws<FileNotFoundException>(() => PoseModel.Load(Path.Combine(AppContext.BaseDirectory, "no-such-model.onnx")));
    }

    [Fact]
    public void Disposed_model_refuses_to_run()
    {
        var model = PoseModel.Load(ModelPath);
        model.Dispose();
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.Run(new byte[320 * 240 * 4], 320, 240, 320 * 4, TimeSpan.Zero));
    }

    [Fact]
    public void Letterbox_maps_points_back_to_the_camera_image()
    {
        // 320 x 240 fits as 192 x 144 with 24 rows of padding above and below.
        var box = Letterbox.For(320, 240, PoseModel.InputSize);
        Assert.Equal(new Letterbox(0, 24, 192, 144, 0.6f), box);

        // Model output (y, x, score) for: the centre, the top-left corner of the picture, a point in the padding.
        float top = 24f / 192, bottom = 168f / 192;
        float[] raw = new float[PoseFrame.PointCount * 3];
        raw[0] = 0.5f; raw[1] = 0.5f; raw[2] = 0.9f;
        raw[3] = top; raw[4] = 0f; raw[5] = 0.8f;
        raw[6] = bottom; raw[7] = 1f; raw[8] = 0.7f;
        raw[9] = 0.01f; raw[10] = 0.5f; raw[11] = 0.6f;

        var pose = PoseModel.Decode(raw, box, TimeSpan.Zero);

        Assert.Equal(new Keypoint(0.5f, 0.5f, 0.9f), pose.Points[0]);
        Assert.Equal(0f, pose.Points[1].X, 4); Assert.Equal(0f, pose.Points[1].Y, 4);
        Assert.Equal(1f, pose.Points[2].X, 4); Assert.Equal(1f, pose.Points[2].Y, 4);
        Assert.Equal(0f, pose.Points[3].Y); // clamped out of the padding
    }
}
