namespace DeskPal.Core.Pose;

/// <summary>The 17 body points a single-person pose model reports, in COCO order.</summary>
public enum BodyPoint
{
    Nose, LeftEye, RightEye, LeftEar, RightEar,
    LeftShoulder, RightShoulder, LeftElbow, RightElbow, LeftWrist, RightWrist,
    LeftHip, RightHip, LeftKnee, RightKnee, LeftAnkle, RightAnkle,
}

/// <summary>One body point. X and Y are 0..1 across the camera image (0,0 = top left, as the camera sees it, not mirrored). Score is the model's confidence, 0..1.</summary>
public readonly record struct Keypoint(float X, float Y, float Score);

/// <summary>
/// One pose result. Holds only body point positions: no image data ever goes in here.
/// Points is always 17 long, indexed by <see cref="BodyPoint"/>.
/// </summary>
public sealed record PoseFrame(TimeSpan Time, IReadOnlyList<Keypoint> Points)
{
    public const int PointCount = 17;

    public Keypoint this[BodyPoint p] => Points[(int)p];
}
