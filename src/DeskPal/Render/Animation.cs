using System.Numerics;
using SharpGLTF.Schema2;

namespace DeskPal.Render;

// Local TRS and morph weights for every node of one model. Preallocated; reset to the node defaults each frame
// because clips carry no constant scale channels.
internal sealed class Pose
{
    public readonly Vector3[] T;
    public readonly Quaternion[] R;
    public readonly Vector3[] S;
    public readonly float[][] W;

    public Pose(GltfModel model)
    {
        var n = model.Nodes.Length;
        T = new Vector3[n];
        R = new Quaternion[n];
        S = new Vector3[n];
        W = model.Nodes.Select(x => new float[x.Weights.Length]).ToArray();
        Reset(model);
    }

    public void Reset(GltfModel model)
    {
        for (var i = 0; i < T.Length; i++)
        {
            var node = model.Nodes[i];
            T[i] = node.Translation;
            R[i] = node.Rotation;
            S[i] = node.Scale;
            node.Weights.CopyTo(W[i], 0);
        }
    }

    public void BlendToward(Pose other, float w)
    {
        if (w <= 0) return;
        w = Math.Min(w, 1);
        for (var i = 0; i < T.Length; i++)
        {
            T[i] = Vector3.Lerp(T[i], other.T[i], w);
            R[i] = Quaternion.Slerp(R[i], other.R[i], w);
            S[i] = Vector3.Lerp(S[i], other.S[i], w);
            var a = W[i];
            var b = other.W[i];
            for (var k = 0; k < a.Length; k++) a[k] += (b[k] - a[k]) * w;
        }
    }
}

internal enum ChannelPath { Translation, Rotation, Scale, Weights }

internal sealed class ClipChannel
{
    public required int Node { get; init; }
    public required ChannelPath Path { get; init; }
    public required bool Step { get; init; }
    public required float[] Times { get; init; }
    public Vector3[]? Vectors { get; init; }
    public Quaternion[]? Rotations { get; init; }
    public float[]? Weights { get; init; }
    public int Stride { get; init; }
}

internal sealed class AnimationClip
{
    public required string Name { get; init; }
    public required float Duration { get; init; }
    public required ClipChannel[] Channels { get; init; }

    public static AnimationClip FromGltf(Animation anim)
    {
        var channels = new List<ClipChannel>();
        foreach (var ch in anim.Channels)
        {
            if (ch.TargetNode is null) continue;
            var node = ch.TargetNode.LogicalIndex;
            switch (ch.TargetNodePath)
            {
                case PropertyPath.translation:
                case PropertyPath.scale:
                {
                    var s = ch.TargetNodePath == PropertyPath.translation ? ch.GetTranslationSampler() : ch.GetScaleSampler();
                    var keys = Keys(s);
                    channels.Add(new ClipChannel
                    {
                        Node = node,
                        Path = ch.TargetNodePath == PropertyPath.translation ? ChannelPath.Translation : ChannelPath.Scale,
                        Step = s.InterpolationMode == AnimationInterpolationMode.STEP,
                        Times = keys.Select(k => k.Time).ToArray(),
                        Vectors = keys.Select(k => k.Value).ToArray(),
                    });
                    break;
                }
                case PropertyPath.rotation:
                {
                    var s = ch.GetRotationSampler();
                    var keys = Keys(s);
                    channels.Add(new ClipChannel
                    {
                        Node = node,
                        Path = ChannelPath.Rotation,
                        Step = s.InterpolationMode == AnimationInterpolationMode.STEP,
                        Times = keys.Select(k => k.Time).ToArray(),
                        Rotations = keys.Select(k => Quaternion.Normalize(k.Value)).ToArray(),
                    });
                    break;
                }
                case PropertyPath.weights:
                {
                    var s = ch.GetMorphSampler();
                    var keys = Keys(s);
                    var stride = keys.Count == 0 ? 0 : Math.Min(keys[0].Value.Length, GltfModel.MaxMorphs);
                    var flat = new float[keys.Count * stride];
                    for (var k = 0; k < keys.Count; k++)
                        for (var j = 0; j < stride; j++) flat[k * stride + j] = keys[k].Value[j];
                    channels.Add(new ClipChannel
                    {
                        Node = node,
                        Path = ChannelPath.Weights,
                        Step = s.InterpolationMode == AnimationInterpolationMode.STEP,
                        Times = keys.Select(k => k.Time).ToArray(),
                        Weights = flat,
                        Stride = stride,
                    });
                    break;
                }
            }
        }
        var duration = channels.Where(c => c.Times.Length > 0).Select(c => c.Times[^1]).DefaultIfEmpty(0).Max();
        return new AnimationClip { Name = anim.Name ?? "", Duration = duration, Channels = channels.Where(c => c.Times.Length > 0).ToArray() };
    }

    private static List<(float Time, T Value)> Keys<T>(IAnimationSampler<T> s) =>
        s.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE
            ? s.GetCubicKeys().Select(k => (k.Item1, k.Item2.Item2)).ToList()
            : s.GetLinearKeys().Select(k => (k.Item1, k.Item2)).ToList();

    public void Sample(float time, Pose pose)
    {
        foreach (var c in Channels)
        {
            var (i, j, f) = Locate(c.Times, time, c.Step);
            switch (c.Path)
            {
                case ChannelPath.Translation:
                    pose.T[c.Node] = Vector3.Lerp(c.Vectors![i], c.Vectors[j], f);
                    break;
                case ChannelPath.Scale:
                    pose.S[c.Node] = Vector3.Lerp(c.Vectors![i], c.Vectors[j], f);
                    break;
                case ChannelPath.Rotation:
                    pose.R[c.Node] = Quaternion.Slerp(c.Rotations![i], c.Rotations[j], f);
                    break;
                case ChannelPath.Weights:
                    var w = pose.W[c.Node];
                    var n = Math.Min(w.Length, c.Stride);
                    for (var k = 0; k < n; k++)
                    {
                        var a = c.Weights![i * c.Stride + k];
                        w[k] = a + (c.Weights[j * c.Stride + k] - a) * f;
                    }
                    break;
            }
        }
    }

    private static (int, int, float) Locate(float[] times, float t, bool step)
    {
        if (t <= times[0]) return (0, 0, 0);
        var last = times.Length - 1;
        if (t >= times[last]) return (last, last, 0);
        var idx = Array.BinarySearch(times, t);
        if (idx >= 0) return (idx, idx, 0);
        var j = ~idx;
        var i = j - 1;
        if (step) return (i, i, 0);
        return (i, j, (t - times[i]) / (times[j] - times[i]));
    }
}

// Layers for Bloop: a looping base clip (idle_breathe), a one-shot or looping action crossfaded in and out over
// 0.2 s (the outgoing action keeps fading while the next one comes in), and a blink morph clip layered on top as a max.
internal sealed class BloopAnimator
{
    private const float Fade = 0.2f;

    private sealed class Layer(AnimationClip clip, float weight, bool loop)
    {
        public AnimationClip Clip { get; } = clip;
        public float MaxWeight { get; } = weight;
        public bool Loop { get; } = loop;
        public float Time;
        public float FadeOutLeft = -1;
        public float FadeFrom;

        public float Weight
        {
            get
            {
                if (FadeOutLeft >= 0) return FadeFrom * FadeOutLeft / Fade;
                var w = Math.Min(1, Time / Fade);
                if (!Loop) w = Math.Min(w, (Clip.Duration - Time) / Fade);
                return Math.Clamp(w, 0, 1) * MaxWeight;
            }
        }

        public void StartFadeOut()
        {
            if (FadeOutLeft >= 0) return;
            FadeFrom = Weight;
            FadeOutLeft = Fade;
        }
    }

    private readonly GltfModel _model;
    private readonly Pose _pose;
    private readonly Pose _scratch;
    private readonly AnimationClip? _base;
    private readonly AnimationClip? _blink;
    private readonly Queue<(AnimationClip Clip, float Weight, bool Loop)> _queue = new();
    private float _baseTime;
    private Layer? _current;
    private Layer? _previous;
    private AnimationClip? _repeat;
    private float _repeatGap;
    private float _gapLeft;
    private float _blinkTime = -1;

    public BloopAnimator(GltfModel model)
    {
        _model = model;
        _pose = new Pose(model);
        _scratch = new Pose(model);
        _base = model.Clips.GetValueOrDefault("idle_breathe");
        _blink = model.Clips.GetValueOrDefault("blink");
    }

    public GltfModel Model => _model;
    public event Action<string>? ClipStarted;

    public bool ActionActive => _current is not null || _previous is not null || _queue.Count > 0 || _repeat is not null;
    public bool Blinking => _blinkTime >= 0;
    public string? CurrentClip => _current?.Clip.Name;

    public void Reset()
    {
        _queue.Clear();
        _current = _previous = null;
        _repeat = null;
        _blinkTime = -1;
    }

    // Replaces whatever action is running (crossfading out of it) and drops anything queued.
    public void Play(string name, float weight = 1, bool loop = false)
    {
        _queue.Clear();
        _repeat = null;
        Enqueue(name, weight, loop);
        StartNext();
    }

    public void Enqueue(string name, float weight = 1, bool loop = false)
    {
        if (_model.Clips.TryGetValue(name, out var clip) && clip.Duration > 0) _queue.Enqueue((clip, weight, loop));
    }

    // Plays the clip again each time it ends, after the gap, until StopRepeat. Starts once the queue is empty.
    public void Repeat(string name, float gap)
    {
        if (!_model.Clips.TryGetValue(name, out var clip) || clip.Duration <= 0) return;
        _repeat = clip;
        _repeatGap = gap;
        _gapLeft = _current is null && _queue.Count == 0 ? 0 : gap;
    }

    // No more repeats; a looping action fades out, a one-shot finishes.
    public void StopRepeat()
    {
        _repeat = null;
        if (_current is { Loop: true }) _current.StartFadeOut();
    }

    public void Blink()
    {
        if (_blink is not null && _blinkTime < 0) _blinkTime = 0;
    }

    public void Advance(float dt, float baseRate)
    {
        if (_base is not null && _base.Duration > 0)
            _baseTime = (_baseTime + dt * baseRate) % _base.Duration;

        if (_previous is not null)
        {
            _previous.Time += dt;
            _previous.FadeOutLeft -= dt;
            if (_previous.FadeOutLeft <= 0) _previous = null;
        }

        if (_current is { } cur)
        {
            cur.Time += dt;
            if (cur.FadeOutLeft >= 0)
            {
                cur.FadeOutLeft -= dt;
                if (cur.FadeOutLeft <= 0) Finish();
            }
            else if (cur.Loop)
            {
                cur.Time %= cur.Clip.Duration;
                if (_queue.Count > 0) StartNext();
            }
            else if (_queue.Count > 0 && cur.Time >= cur.Clip.Duration - Fade) StartNext();
            else if (cur.Time >= cur.Clip.Duration) Finish();
        }
        else if (_queue.Count > 0) StartNext();
        else if (_repeat is not null)
        {
            _gapLeft -= dt;
            if (_gapLeft <= 0)
            {
                _queue.Enqueue((_repeat, 1, false));
                StartNext();
            }
        }

        if (_blinkTime >= 0)
        {
            _blinkTime += dt;
            if (_blink is null || _blinkTime > _blink.Duration) _blinkTime = -1;
        }

        void Finish()
        {
            _current = null;
            _gapLeft = _repeatGap;
        }
    }

    private void StartNext()
    {
        if (_queue.Count == 0) return;
        var (clip, weight, loop) = _queue.Dequeue();
        if (_current is not null)
        {
            _current.StartFadeOut();
            _previous = _current;
        }
        _current = new Layer(clip, weight, loop);
        ClipStarted?.Invoke(clip.Name);
    }

    public Pose Evaluate()
    {
        _pose.Reset(_model);
        _base?.Sample(_baseTime, _pose);
        Mix(_previous);
        Mix(_current);
        if (_blink is not null && _blinkTime >= 0)
        {
            _scratch.Reset(_model);
            _blink.Sample(_blinkTime, _scratch);
            for (var n = 0; n < _pose.W.Length; n++)
            {
                var a = _pose.W[n];
                var b = _scratch.W[n];
                for (var k = 0; k < a.Length; k++) a[k] = Math.Max(a[k], b[k]);
            }
        }
        return _pose;

        void Mix(Layer? layer)
        {
            if (layer is null) return;
            var w = layer.Weight;
            if (w <= 0) return;
            _scratch.Reset(_model);
            layer.Clip.Sample(Math.Min(layer.Time, layer.Clip.Duration), _scratch);
            _pose.BlendToward(_scratch, w);
        }
    }
}
