# ManagedCodecs video for Godot (.NET)

A `TextureRect` node, `ManagedCodecsVideo`, that plays an MP4, Matroska, WebM or AVI file (H.264 or MJPEG video, AAC
audio) in Godot 4's .NET build. Godot itself plays only Ogg Theora; this decodes in managed C# with the
[ManagedCodecs](https://github.com/bisforboman/ManagedCodecs) packages, so the same file plays on every desktop platform
the .NET build exports to. Not for web exports: Godot's C# can't export to the web yet.

**Run in Godot 4.7.2:** on Windows, 480p and 1080p H.264 (issue #402), and on Linux in the editor's runner and an
exported build (under Xvfb with software OpenGL and no sound card): every frame shown, the picture upright and its
colours FFmpeg's, the sound in sync. Sound on a desktop and macOS are not checked yet. A report helps (repository
README, "Testing it in Godot").

## Installing

1. Godot 4.7 or later, the .NET build, with the .NET 10 SDK. In the project's `.csproj`, target `net10.0` and add the
   decoders:

   ```xml
   <PropertyGroup>
     <TargetFramework>net10.0</TargetFramework>
   </PropertyGroup>
   <ItemGroup>
     <PackageReference Include="ManagedCodecs.Playback" Version="*" />
   </ItemGroup>
   ```

   (`*` takes the newest; pin the version you tested with. `ManagedCodecs.Playback` is in releases after 0.8.1.)
2. Copy this folder to the project's `addons/managedcodecs/` and build (the hammer icon, or `dotnet build`).
3. Add a `ManagedCodecsVideo` node (Add Node lists it once built), set its `File Path` to the video (`res://` paths
   play from an exported `.pck` too) and, for a menu's backdrop, `Loop`.
4. To export: a video file isn't a Godot resource, so add its pattern (`*.mp4`) to the export preset's "Filters to
   export non-resource files/folders" (Resources tab), or it is left out of the `.pck` and the node can't read it. A C#
   project exports from its solution (`<project>.sln`, which the editor creates once asked to build).

The node reads the whole file into memory (right for cutscenes, not for films), keeps the picture's aspect ratio
(`StretchMode` keep-aspect-centred), streams the sound through an `AudioStreamPlayer` child and prints a line every 5
seconds: frames shown and dropped, the time the decoding thread waited for the decoder a frame (near zero while its
threads keep ahead) and the RGBA conversion's time a frame. Built against the repository's sources instead of the
package, reference them in Release (`SetConfiguration="Configuration=Release"`, as this repository's
`ManagedCodecs.Godot.csproj` does): Godot's own configurations leave them unoptimised, several times slower, and the
node warns. A phone video's rotation is
not applied; turn the node.

MIT licensed (LICENSE). H.264 is covered by patents in some countries (the Via LA AVC pool): whether a game that
decodes it needs a licence there is for its publisher to check.
