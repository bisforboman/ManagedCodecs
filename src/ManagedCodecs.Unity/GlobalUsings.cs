// The AAC sources find Shared\Vlc.cs in their own namespace in ManagedCodecs.Aac.dll; here it is compiled once, into
// ManagedCodecs.H264 (the H.264 sources' own namespace wins over this alias).
global using Vlc = ManagedCodecs.H264.Vlc;
