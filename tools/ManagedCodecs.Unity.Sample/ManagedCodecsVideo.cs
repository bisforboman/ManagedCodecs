using System.Collections.Concurrent;
using ManagedCodecs.Playback;
using UnityEngine;

namespace ManagedCodecs.UnitySample;

/// <summary>
/// Plays an MP4 or AVI file (H.264 or MJPEG, AAC or MP3 audio) from StreamingAssets onto this object's renderer, decoded by
/// ManagedCodecs.Unity: a thread of its own decodes ahead (<see cref="MediaPlayer"/>), <see cref="Update"/> shows the
/// newest frame due by a clock started at the first frame (late ones are dropped), and the audio streams
/// through an <see cref="AudioClip"/> whose callback, on Unity's audio thread, takes the decoded samples. Not for
/// WebGL, which has no threads. Not a media player: no seeking, no pause.
/// </summary>
public sealed class ManagedCodecsVideo : MonoBehaviour
{
    /// <summary>The file's name in StreamingAssets.</summary>
    [SerializeField]
    public string fileName = "video.mp4";

    private readonly ConcurrentQueue<float> _samples = new();
    private MediaPlayer? _player;
    private Renderer? _renderer;
    private Texture2D? _texture;
    private float _start = -1;
    private int _audioRate;
    private int _shown;
    private float _lastReport;

    private void Start()
    {
        // StreamingAssets is a folder on desktop; on Android it lies inside the APK, where a file is read through
        // UnityWebRequest into a MemoryStream instead (Demuxer.Open takes any seekable stream).
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
        try
        {
            _player = new MediaPlayer(path);
        }
        catch (System.Exception e) when (e is System.NotSupportedException or System.IO.InvalidDataException or System.IO.IOException)
        {
            Debug.LogError($"ManagedCodecsVideo: cannot play {path}: {e.Message}");
            enabled = false;
            return;
        }

        if (_player.SkippedAudioCodec is { } codec)
        {
            Debug.LogWarning($"ManagedCodecsVideo: {fileName}'s audio is {codec}, which isn't decoded (only AAC and MP3 are); playing without sound.");
        }

        _renderer = GetComponent<Renderer>();
        _player.Start();
    }

    private void Update()
    {
        if (_player is null || _renderer is null)
        {
            return;
        }

        // The clock starts with the first frame shown (due at zero).
        var due = _player.TakeFrame(System.TimeSpan.FromSeconds(_start < 0 ? 0 : Time.time - _start));
        if (due is not null)
        {
            if (_start < 0)
            {
                _start = Time.time;
            }

            Show(due);
            _player.Return(due);
            _shown++;
        }

        StartAudio();

        if (Time.time - _lastReport >= 5)
        {
            _lastReport = Time.time;
            Report();
        }

        if (_player.IsFinished)
        {
            if (_player.Error is { } error)
            {
                Debug.LogError($"ManagedCodecsVideo: {fileName} stopped early: {error.Message}");
            }

            Report();
            enabled = false;
        }
    }

    // Whether playback keeps up: a frame is dropped when a later one was due by the time it could be shown, so frames
    // dropped steadily mean decoding or the RGBA conversion (each timed per frame) is slower than real time.
    private void Report()
    {
        var statistics = _player!.Statistics;
        Debug.Log($"ManagedCodecsVideo: {_player.Video.Width}x{_player.Video.Height}, {_shown} frames shown, {statistics.DroppedFrames} dropped, ms a frame: decode {statistics.DecodeMilliseconds:F1}, RGBA conversion {statistics.ConvertMilliseconds:F1}");
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

    // The audio's clip, streaming from the decoded samples, made and played once the first frame is shown, so that the
    // sound starts with the picture's clock: the first audio comes out of the decoder before the first frame (frame
    // threading's delay and more), and is queued here until then. The first samples give the rate (HE-AAC's output is
    // twice its core's).
    private void StartAudio()
    {
        while (_player!.TakeAudio() is { } chunk)
        {
            _audioRate = chunk.SampleRate;
            foreach (float sample in chunk.Samples)
            {
                _samples.Enqueue(sample);
            }
        }

        if (_audioRate == 0 || _start < 0 || GetComponent<AudioSource>() is { clip: not null })
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
        if (_player is not null && _shown > 0 && enabled)
        {
            Report();
        }

        _player?.Dispose();
    }
}
