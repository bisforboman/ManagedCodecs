using System.Collections.Concurrent;
using ManagedCodecs.MonoGame;
using UnityEngine;

namespace ManagedCodecs.UnitySample;

/// <summary>
/// Plays an MP4 or AVI file (H.264 or MJPEG, AAC audio) from StreamingAssets onto this object's renderer, decoded by
/// ManagedCodecs.Unity: a thread of its own decodes ahead (<see cref="MediaSource"/>, mcplay's), <see cref="Update"/>
/// shows the newest frame due by a clock started at the first frame and drops late ones, and the audio streams
/// through an <see cref="AudioClip"/> whose callback, on Unity's audio thread, takes the decoded samples. Not for
/// WebGL, which has no threads. Not a media player: no seeking, no pause.
/// </summary>
public sealed class ManagedCodecsVideo : MonoBehaviour
{
    /// <summary>The file's name in StreamingAssets.</summary>
    [SerializeField]
    public string fileName = "video.mp4";

    private readonly ConcurrentQueue<float> _samples = new();
    private MediaSource? _source;
    private Renderer? _renderer;
    private Texture2D? _texture;
    private RgbaFrame? _next;
    private float _start = -1;
    private int _audioRate;
    private int _shown;
    private int _dropped;
    private float _lastReport;

    private void Start()
    {
        // StreamingAssets is a folder on desktop; on Android it lies inside the APK, where a file is read through
        // UnityWebRequest into a MemoryStream instead (Demuxer.Open takes any seekable stream).
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
        try
        {
            _source = new MediaSource(path);
        }
        catch (System.Exception e) when (e is System.NotSupportedException or System.IO.InvalidDataException or System.IO.IOException)
        {
            Debug.LogError($"ManagedCodecsVideo: cannot play {path}: {e.Message}");
            enabled = false;
            return;
        }

        _renderer = GetComponent<Renderer>();
        _source.Start();
    }

    private void Update()
    {
        if (_source is null || _renderer is null)
        {
            return;
        }

        StartAudio();

        // The newest frame due: frames come in display order, so take them while the next one's time has come.
        while (_next is null && _source.Frames.TryTake(out var frame))
        {
            _next = frame;
        }

        if (_next is null)
        {
            return;
        }

        if (_start < 0)
        {
            _start = Time.time - (float)_next.Time;
        }

        RgbaFrame? due = null;
        while (_next is not null && _next.Time <= Time.time - _start)
        {
            if (due is not null)
            {
                _dropped++;
            }

            due = _next;
            _next = _source.Frames.TryTake(out var frame) ? frame : null;
        }

        if (due is not null)
        {
            Show(due);
            _shown++;
        }

        if (Time.time - _lastReport >= 5)
        {
            _lastReport = Time.time;
            Report();
        }
    }

    // Whether playback keeps up: a frame is dropped when a later one was due by the time it could be shown, so frames
    // dropped steadily mean decoding (or the RGBA conversion, timed on its own) is slower than real time.
    private void Report()
    {
        var (_, convert) = _source!.ConvertStatistics;
        Debug.Log($"ManagedCodecsVideo: {_source.VideoStream.Width}x{_source.VideoStream.Height}, {_shown} frames shown, {_dropped} dropped, RGBA conversion {convert:F1} ms a frame");
    }

    // A Texture2D of the frame's size, filled with its RGBA rows. Unity's textures start at the bottom row, so the
    // material flips the image vertically rather than every frame being turned over in memory.
    private void Show(RgbaFrame frame)
    {
        if (_texture is null || _texture.width != frame.Width || _texture.height != frame.Height)
        {
            _texture = new Texture2D(frame.Width, frame.Height, TextureFormat.RGBA32, false);
            var material = _renderer!.material;
            material.mainTexture = _texture;
            material.mainTextureScale = new Vector2(1, -1);
            material.mainTextureOffset = new Vector2(0, 1);
        }

        _texture.LoadRawTextureData(frame.Pixels);
        _texture.Apply(false);
    }

    // The audio's clip, made once the first samples give the rate (HE-AAC's output is twice its core's), streaming from
    // the decoded samples.
    private void StartAudio()
    {
        while (_source!.Audio.TryTake(out var chunk))
        {
            _audioRate = chunk.SampleRate;
            foreach (short sample in chunk.Samples)
            {
                _samples.Enqueue(sample / 32768f);
            }
        }

        if (_audioRate == 0 || GetComponent<AudioSource>() is { clip: not null })
        {
            return;
        }

        var audio = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
        audio.clip = AudioClip.Create("ManagedCodecs", _audioRate * 2, 2, _audioRate, true, OnAudioRead);
        audio.loop = true; // a streamed clip is read for as long as it plays
        audio.Play();
    }

    // Unity's audio thread: interleaved stereo, silence where decoding has not caught up.
    private void OnAudioRead(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = _samples.TryDequeue(out float sample) ? sample : 0;
        }
    }

    private void OnDestroy()
    {
        if (_source is not null && _shown > 0)
        {
            Report();
        }

        _source?.Dispose();
    }
}
