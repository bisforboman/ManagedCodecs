# The Godot Asset Library listing

The addon (`../addons/managedcodecs/`) on [godotengine.org/asset-library](https://godotengine.org/asset-library/asset),
issue #352. The owner submits and updates it under their account; this folder holds what the listing links to and the
text to fill in.

## How it is downloaded

The Asset Library installs a zip's files at their paths in it, so it can't take this repository's own archive (the
addon would land in `res://tools/ManagedCodecs.Godot/addons/`). Each release attaches
`managedcodecs-godot-X.Y.Z.zip` to its GitHub release instead (`release.yml`, `tools/godot-addon.sh`): only
`addons/managedcodecs/`, its README's package reference pinned to that version. The listing uses the "Custom"
repository host with that zip as its download URL. After each release: edit the listing's version and download URL.

## The submission

| Field | Value |
| --- | --- |
| Asset name | ManagedCodecs Video (H.264/MJPEG, AAC; MP4, MKV, AVI) |
| Category | Scripts |
| Godot version | 4.7 |
| Version | 0.9.1 (the release whose zip it downloads; the first with one) |
| Repository host | Custom |
| Repository URL | https://github.com/bisforboman/ManagedCodecs |
| Issues URL | https://github.com/bisforboman/ManagedCodecs/issues |
| Download URL | https://github.com/bisforboman/ManagedCodecs/releases/download/v0.9.1/managedcodecs-godot-0.9.1.zip |
| Icon URL | https://raw.githubusercontent.com/bisforboman/ManagedCodecs/main/tools/ManagedCodecs.Godot/listing/icon.png |
| License | MIT |
| Previews | image: `demo.gif`, thumbnail `still.jpg` (raw.githubusercontent.com URLs, as the icon's) |

Description:

> Play MP4, Matroska, WebM and AVI video in Godot 4 (.NET) with a node: `ManagedCodecsVideo`, a TextureRect that
> plays H.264 or MJPEG video with AAC sound. Godot itself plays only Ogg Theora; this decodes in managed C# (the
> ManagedCodecs NuGet packages), with no native libraries, so the same file plays on every desktop platform the .NET
> build exports to.
>
> Needs Godot 4.7 or later (.NET build) and the .NET 10 SDK. Add the package to your project's .csproj (net10.0):
> `<PackageReference Include="ManagedCodecs.Playback" Version="0.9.1" />`, add a ManagedCodecsVideo node and set its
> File Path. To export, add `*.mp4` to the export preset's non-resource filter. The addon's README has the details.
>
> Plays from start to end, optionally looped (cutscenes, menu backdrops); no seeking or pausing. The whole file is read
> into memory. Not for web exports (Godot's C# can't export to the web yet).
>
> H.264 is covered by patents in some countries (the Via LA AVC pool): whether a game that decodes it needs a licence
> there is for its publisher to check.

## The images

- `icon.png`: 256 x 256, drawn for the listing (MIT, as the addon).
- `demo.gif` (6 s, 480 px wide) and `still.jpg`: the sample playing in Godot 4.7.2, recorded in Movie Maker mode
  (`--write-movie`) from a clip of *Big Buck Bunny* (1:35 to 1:43), © 2008 Blender Foundation, www.bigbuckbunny.org,
  licensed [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). Not committed as video: the README says where to
  download it.
