// Not Unity's API: the UnityEngine members ManagedCodecsVideo.cs calls, declared so that CI compiles the sample against
// the Unity build without a Unity install. Their bodies do nothing. Don't copy this file into a Unity project.
#pragma warning disable

namespace UnityEngine
{
    public class Object
    {
    }

    public class Component : Object
    {
        public T GetComponent<T>() => default!;

        public T AddComponent<T>() => default!;
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; }
    }

    public class MonoBehaviour : Behaviour
    {
        public GameObject gameObject => default!;
    }

    public sealed class GameObject : Object
    {
        public T AddComponent<T>() => default!;
    }

    public sealed class SerializeFieldAttribute : System.Attribute
    {
    }

    public static class Application
    {
        public static string streamingAssetsPath => string.Empty;
    }

    public static class Time
    {
        public static float time => 0;
    }

    public static class Debug
    {
        public static void Log(object message)
        {
        }

        public static void LogError(object message)
        {
        }
    }

    public struct Vector2
    {
        public Vector2(float x, float y)
        {
        }
    }

    public enum TextureFormat
    {
        RGBA32 = 4,
    }

    public class Texture : Object
    {
    }

    public sealed class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipChain)
        {
        }

        public int width => 0;

        public int height => 0;

        public void LoadRawTextureData(byte[] data)
        {
        }

        public void Apply(bool updateMipmaps)
        {
        }
    }

    public sealed class Material : Object
    {
        public Texture mainTexture { get; set; } = default!;

        public Vector2 mainTextureScale { get; set; }

        public Vector2 mainTextureOffset { get; set; }
    }

    public class Renderer : Component
    {
        public Material material { get; set; } = default!;
    }

    public sealed class AudioClip : Object
    {
        public delegate void PCMReaderCallback(float[] data);

        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream, PCMReaderCallback pcmreadercallback) => default!;
    }

    public sealed class AudioSource : Behaviour
    {
        public AudioClip clip { get; set; } = default!;

        public bool loop { get; set; }

        public void Play()
        {
        }
    }
}
