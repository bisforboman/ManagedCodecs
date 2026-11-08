// The AAC and MP3 sources find Shared\*.cs in their own namespace in their own assemblies; here it is compiled once, into
// ManagedCodecs.H264 (the H.264 sources' own namespace wins over this alias).
global using ExactMath = ManagedCodecs.H264.ExactMath;
global using Vlc = ManagedCodecs.H264.Vlc;
